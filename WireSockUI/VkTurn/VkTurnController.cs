using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WireSockUI.Config;
using WireSockUI.Native;

namespace WireSockUI.VkTurn
{
    internal enum VkPrepareResult
    {
        NotVkProfile,
        Ready,
        Failed,
        Cancelled
    }

    /// <summary>
    ///     Glue between the main window and <see cref="VkTurnSession" />: starts the core
    ///     before WireSock connects, shows captcha windows, writes the runtime config that
    ///     wgbooster.dll loads, and tears everything down on disconnect.
    /// </summary>
    internal sealed class VkTurnController : IDisposable
    {
        private static readonly TimeSpan TimeoutPerLink = TimeSpan.FromMinutes(6);
        private static readonly ConcurrentDictionary<string, string> RuntimeConfigPaths =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly Control _owner;
        private readonly Action<string> _log;
        private readonly Dictionary<string, VkCaptchaForm> _openCaptchas = new Dictionary<string, VkCaptchaForm>();
        private VkTurnSession _session;
        private CancellationTokenSource _prepareCts;
        private Task _stopTask = Task.CompletedTask;

        public VkTurnController(Control owner, Action<string> log)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _log = log ?? (_ => { });
        }

        /// <summary>Raised on the UI thread when the core dies while the tunnel is up.</summary>
        public event Action<string, string> UnexpectedExit;

        public string ActiveProfile { get; private set; }
        public string LastError { get; private set; }
        public bool IsRunning => _session != null;

        internal static string RuntimeFolder => Path.Combine(Global.SecureMainFolder, "VkTurn");

        /// <summary>wgbooster.dll loads this file instead of the stored profile while VK TURN is active.</summary>
        public static string ResolveConnectPath(string profile)
        {
            return profile != null && RuntimeConfigPaths.TryGetValue(profile, out var path) ? path : null;
        }

        public static bool IsVkProfile(string profile)
        {
            try
            {
                var text = SecureFileSystem.ReadAllText(Profile.GetProfilePath(profile), Profile.MaxProfileSizeBytes);
                return VkTurnSettings.HasDirectives(text);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        ///     Must be called on the UI thread before the WireSock connect. For a VK TURN
        ///     profile it starts the core and returns once the first stream is up.
        /// </summary>
        public async Task<VkPrepareResult> PrepareAsync(string profile)
        {
            LastError = null;
            await _stopTask.ConfigureAwait(true);

            string profileText;
            VkTurnSettings settings;
            try
            {
                profileText = SecureFileSystem.ReadAllText(Profile.GetProfilePath(profile), Profile.MaxProfileSizeBytes);
                settings = VkTurnSettings.Parse(profileText);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return VkPrepareResult.Failed;
            }

            if (settings == null)
                return VkPrepareResult.NotVkProfile;

            string runtimePath;
            try
            {
                runtimePath = WriteRuntimeConfig(profile, settings.BuildEffectiveConfig(profileText));
            }
            catch (Exception ex)
            {
                LastError = $"Unable to prepare the VK TURN runtime configuration: {ex.Message}";
                return VkPrepareResult.Failed;
            }

            var session = new VkTurnSession();
            session.Log += _log;
            session.CaptchaRequired += OnCaptchaRequired;
            session.CaptchaClosed += OnCaptchaClosed;
            session.UnexpectedExit += error => PostToUi(() => OnUnexpectedExit(profile, error));

            var cts = new CancellationTokenSource();
            _prepareCts = cts;
            _session = session;
            ActiveProfile = profile;

            var externalCaptcha = VkCaptchaForm.IsRuntimeAvailable();
            if (!externalCaptcha)
                _log("[VK TURN] Microsoft Edge WebView2 is not installed; the captcha will open in the default browser.");

            bool established;
            try
            {
                established = await session.StartAsync(settings, externalCaptcha,
                    Path.Combine(RuntimeFolder, "vk-creds.bin"), TimeoutPerLink, cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                established = false;
            }

            if (established && !cts.IsCancellationRequested)
            {
                RuntimeConfigPaths[profile] = runtimePath;
                return VkPrepareResult.Ready;
            }

            var cancelled = cts.IsCancellationRequested;
            LastError = cancelled ? null : session.LastError ?? "VK TURN could not connect.";
            StopInBackground();
            return cancelled ? VkPrepareResult.Cancelled : VkPrepareResult.Failed;
        }

        /// <summary>Stops the core without blocking the UI thread. Safe to call repeatedly.</summary>
        public void StopInBackground()
        {
            var session = _session;
            var profile = ActiveProfile;
            _session = null;
            ActiveProfile = null;
            _prepareCts?.Cancel();
            _prepareCts = null;
            CloseAllCaptchas();

            if (profile != null && RuntimeConfigPaths.TryRemove(profile, out var path))
                TryDelete(path);

            if (session == null)
                return;

            var previous = _stopTask;
            _stopTask = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                session.Dispose();
            });
        }

        public void Dispose()
        {
            StopInBackground();
            try
            {
                _stopTask.Wait(6000);
            }
            catch
            {
                // shutting down anyway
            }
        }

        private void OnCaptchaRequired(VkTurnCaptchaRequest request)
        {
            PostToUi(() =>
            {
                var session = _session;
                if (session == null || string.IsNullOrEmpty(request.RedirectUri))
                    return;

                var form = new VkCaptchaForm(request.RedirectUri);
                _openCaptchas[request.Id] = form;
                form.FormClosed += (s, e) =>
                {
                    _openCaptchas.Remove(request.Id);
                    if (!ReferenceEquals(session, _session))
                        return;
                    if (form.DialogResult == DialogResult.OK && !string.IsNullOrEmpty(form.SuccessToken))
                        session.SubmitCaptchaToken(request.Id, form.SuccessToken);
                    else
                        session.CancelCaptcha(request.Id);
                    form.Dispose();
                };
                form.Show(_owner as IWin32Window);
                form.Activate();
            });
        }

        private void OnCaptchaClosed(string id)
        {
            PostToUi(() =>
            {
                if (id != null && _openCaptchas.TryGetValue(id, out var form) && !form.IsDisposed)
                    form.Close();
            });
        }

        private void CloseAllCaptchas()
        {
            foreach (var form in new List<VkCaptchaForm>(_openCaptchas.Values))
                if (!form.IsDisposed)
                    form.Close();
            _openCaptchas.Clear();
        }

        private void OnUnexpectedExit(string profile, string error)
        {
            if (!string.Equals(profile, ActiveProfile, StringComparison.OrdinalIgnoreCase))
                return;
            LastError = error;
            UnexpectedExit?.Invoke(profile, error);
        }

        private void PostToUi(Action action)
        {
            if (_owner.IsDisposed || !_owner.IsHandleCreated)
                return;
            try
            {
                _owner.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // window is closing
            }
        }

        private static string WriteRuntimeConfig(string profile, string content)
        {
            Global.EnsureSecureMainFolderExists();
            Directory.CreateDirectory(RuntimeFolder); // inherits the administrators-only ACL
            var path = Path.Combine(RuntimeFolder, profile + ".conf");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // a leftover runtime copy is overwritten on the next connect
            }
        }
    }
}
