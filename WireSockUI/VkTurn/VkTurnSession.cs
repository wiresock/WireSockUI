using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WireSockUI.VkTurn
{
    internal enum VkTurnPhase
    {
        Stopped,
        Starting,
        WaitingForCaptcha,
        Established,
        Failed
    }

    internal sealed class VkTurnStats
    {
        public ulong RxBytes { get; set; }
        public ulong TxBytes { get; set; }

        /// <summary>Seconds since the last WireGuard handshake, or -1 before the first one.</summary>
        public long HandshakeAgoSeconds { get; set; }
    }

    internal sealed class VkTurnCaptchaRequest
    {
        public string Id { get; set; }
        public string RedirectUri { get; set; }
        public int Stream { get; set; }
    }

    /// <summary>
    ///     Owns one vkturn-core.exe child process and its JSON event channel
    ///     (see core/client/events.go). All events are raised on thread-pool threads.
    /// </summary>
    internal sealed class VkTurnSession : IDisposable
    {
        internal const string CoreFileName = "vkturn-core.exe";

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };

        private readonly object _sync = new object();
        private Process _process;
        private TaskCompletionSource<bool> _establishedTcs;
        private bool _stopping;
        private bool _disposed;

        public event Action<string> Log;
        public event Action<VkTurnCaptchaRequest> CaptchaRequired;
        public event Action<string> CaptchaClosed;
        public event Action<VkTurnPhase> PhaseChanged;
        public event Action<VkTurnStats> StatsUpdated;

        /// <summary>Raised when the core exits on its own after it was established.</summary>
        public event Action<string> UnexpectedExit;

        public VkTurnPhase Phase { get; private set; } = VkTurnPhase.Stopped;
        public int ConnectedStreams { get; private set; }
        public string TurnIp { get; private set; }
        public string LastError { get; private set; }

        public static string CorePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CoreFileName);

        /// <summary>
        ///     Starts the core and waits until the first DTLS session is up. Each link in the
        ///     profile is tried in turn if the core gives up before establishing.
        /// </summary>
        public async Task<bool> StartAsync(VkTurnSettings settings, bool externalCaptcha, string credsCachePath,
            string wireGuardConfigPath, TimeSpan timeoutPerLink, CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (!File.Exists(CorePath))
            {
                LastError = $"{CoreFileName} was not found next to the application ({CorePath}).";
                SetPhase(VkTurnPhase.Failed);
                return false;
            }

            for (var i = 0; i < settings.Links.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (settings.Links.Count > 1)
                    WriteLog($"Using VK call link {i + 1} of {settings.Links.Count}.");

                var established = await RunUntilEstablishedAsync(settings, i, externalCaptcha, credsCachePath,
                    wireGuardConfigPath, timeoutPerLink, cancellationToken).ConfigureAwait(false);
                if (established)
                    return true;

                Stop();
                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            SetPhase(VkTurnPhase.Failed);
            return false;
        }

        private async Task<bool> RunUntilEstablishedAsync(VkTurnSettings settings, int linkIndex,
            bool externalCaptcha, string credsCachePath, string wireGuardConfigPath, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var startInfo = new ProcessStartInfo(CorePath,
                settings.BuildCoreArguments(linkIndex, externalCaptcha, credsCachePath, wireGuardConfigPath))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (s, e) => { if (e.Data != null) HandleEventLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) WriteLog(StripGoTimestamp(e.Data)); };
            process.Exited += (s, e) => OnProcessExited(process);

            lock (_sync)
            {
                if (_disposed) return false;
                _stopping = false;
                _establishedTcs = tcs;
                _process = process;
                ConnectedStreams = 0;
                TurnIp = null;
                LastError = null;
            }

            SetPhase(VkTurnPhase.Starting);
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                LastError = $"Unable to start {CoreFileName}: {ex.Message}";
                WriteLog(LastError);
                return false;
            }

            process.StandardInput.AutoFlush = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            WriteLog($"{CoreFileName} started (pid {process.Id}).");

            using (var timeoutCts = new CancellationTokenSource(timeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken))
            using (linked.Token.Register(() => tcs.TrySetResult(false)))
            {
                var ok = await tcs.Task.ConfigureAwait(false);
                if (!ok && timeoutCts.IsCancellationRequested && LastError == null)
                    LastError = $"VK TURN did not connect within {timeout.TotalMinutes:0} minutes.";
                return ok;
            }
        }

        public void SubmitCaptchaToken(string id, string token)
        {
            SendCommand(new Dictionary<string, object> { { "cmd", "captcha_token" }, { "id", id }, { "token", token } });
        }

        public void CancelCaptcha(string id)
        {
            SendCommand(new Dictionary<string, object> { { "cmd", "captcha_cancel" }, { "id", id } });
        }

        /// <summary>Asks the core to exit, then kills it if it does not leave within 3 seconds.</summary>
        public void Stop()
        {
            Process process;
            lock (_sync)
            {
                process = _process;
                _process = null;
                _stopping = true;
                _establishedTcs?.TrySetResult(false);
            }

            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        TryWrite(process, "{\"cmd\":\"shutdown\"}");
                        try { process.StandardInput.Close(); } catch { /* already closed */ }
                        if (!process.WaitForExit(3000))
                        {
                            WriteLog($"{CoreFileName} did not exit in time; terminating it.");
                            process.Kill();
                            process.WaitForExit(2000);
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteLog($"Stopping {CoreFileName} failed: {ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (Phase != VkTurnPhase.Failed)
                SetPhase(VkTurnPhase.Stopped);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }

            Stop();
        }

        private void HandleEventLine(string line)
        {
            Dictionary<string, object> ev;
            try
            {
                ev = Json.Deserialize<Dictionary<string, object>>(line);
            }
            catch
            {
                WriteLog(line); // not an event: plain output
                return;
            }

            if (ev == null || !ev.TryGetValue("event", out var nameObj))
                return;

            switch (nameObj as string)
            {
                case "starting":
                    WriteLog("VK TURN core is starting.");
                    break;
                case "captcha_required":
                    SetPhase(VkTurnPhase.WaitingForCaptcha);
                    WriteLog("VK asks for a captcha.");
                    CaptchaRequired?.Invoke(new VkTurnCaptchaRequest
                    {
                        Id = GetString(ev, "id"),
                        RedirectUri = GetString(ev, "redirect_uri"),
                        Stream = GetInt(ev, "stream")
                    });
                    break;
                case "captcha_solved":
                case "captcha_failed":
                    CaptchaClosed?.Invoke(GetString(ev, "id"));
                    if (Phase == VkTurnPhase.WaitingForCaptcha)
                        SetPhase(ConnectedStreams > 0 ? VkTurnPhase.Established : VkTurnPhase.Starting);
                    if ((string)nameObj == "captcha_failed")
                        WriteLog($"Captcha was not solved: {GetString(ev, "error")}");
                    break;
                case "turn_ip":
                    TurnIp = GetString(ev, "ip");
                    break;
                case "stream_up":
                case "stream_down":
                    ConnectedStreams = GetInt(ev, "connected");
                    break;
                case "established":
                    WriteLog("VK TURN tunnel to the server is established.");
                    SetPhase(VkTurnPhase.Established);
                    lock (_sync) _establishedTcs?.TrySetResult(true);
                    break;
                case "wg_handshake":
                    WriteLog("WireGuard handshake with the server completed.");
                    break;
                case "wg_stats":
                    StatsUpdated?.Invoke(new VkTurnStats
                    {
                        RxBytes = (ulong)Math.Max(0, GetLong(ev, "rx_bytes")),
                        TxBytes = (ulong)Math.Max(0, GetLong(ev, "tx_bytes")),
                        HandshakeAgoSeconds = GetLong(ev, "handshake_ago")
                    });
                    break;
                case "vk_backoff":
                    WriteLog($"VK is refusing requests; retrying in {GetInt(ev, "seconds")} s.");
                    break;
                case "fatal":
                    LastError = GetString(ev, "error");
                    WriteLog($"VK TURN core stopped: {LastError}");
                    break;
            }
        }

        private void OnProcessExited(Process process)
        {
            bool expected;
            bool wasEstablished;
            lock (_sync)
            {
                if (!ReferenceEquals(process, _process) && _process != null)
                    return; // an older process
                expected = _stopping;
                wasEstablished = _establishedTcs != null && _establishedTcs.Task.IsCompleted &&
                                 _establishedTcs.Task.Status == TaskStatus.RanToCompletion &&
                                 _establishedTcs.Task.Result;
                _establishedTcs?.TrySetResult(false);
            }

            int code;
            try { code = process.ExitCode; } catch { code = -1; }
            if (expected)
                return;

            if (LastError == null)
                LastError = $"{CoreFileName} exited unexpectedly (code {code}).";
            WriteLog(LastError);
            SetPhase(VkTurnPhase.Failed);
            if (wasEstablished)
                UnexpectedExit?.Invoke(LastError);
        }

        private void SendCommand(Dictionary<string, object> command)
        {
            Process process;
            lock (_sync) process = _process;
            if (process != null)
                TryWrite(process, Json.Serialize(command));
        }

        private void TryWrite(Process process, string line)
        {
            try
            {
                process.StandardInput.WriteLine(line);
            }
            catch (Exception ex)
            {
                WriteLog($"Unable to talk to {CoreFileName}: {ex.Message}");
            }
        }

        private void SetPhase(VkTurnPhase phase)
        {
            if (Phase == phase) return;
            Phase = phase;
            PhaseChanged?.Invoke(phase);
        }

        private void WriteLog(string message)
        {
            Log?.Invoke("[VK TURN] " + message);
        }

        private static string StripGoTimestamp(string line)
        {
            // Go's log package prefixes "2006/01/02 15:04:05 "; the UI log has its own timestamps.
            return line.Length > 20 && line[4] == '/' && line[7] == '/' && line[10] == ' ' && line[19] == ' '
                ? line.Substring(20)
                : line;
        }

        private static string GetString(Dictionary<string, object> ev, string key)
        {
            return ev.TryGetValue(key, out var v) && v != null ? Convert.ToString(v) : null;
        }

        private static long GetLong(Dictionary<string, object> ev, string key)
        {
            try
            {
                return ev.TryGetValue(key, out var v) && v != null ? Convert.ToInt64(v) : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static int GetInt(Dictionary<string, object> ev, string key)
        {
            try
            {
                return ev.TryGetValue(key, out var v) && v != null ? Convert.ToInt32(v) : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
