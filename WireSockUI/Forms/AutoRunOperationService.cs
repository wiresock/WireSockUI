using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WireSockUI.Forms
{
    internal enum AutoRunHelperOperation
    {
        Inspect,
        Enable,
        EnableMigratingLegacyTask,
        Disable,
        DisableMigratingLegacyTask,
        DeleteLegacyShortcut
    }

    internal enum AutoRunOperationOutcome
    {
        Succeeded,
        Failed,
        TimedOut,
        StateUncertain
    }

    internal sealed class AutoRunOperationResult
    {
        internal AutoRunOperationResult(
            AutoRunOperationOutcome outcome,
            string payload = null,
            string diagnostic = null,
            bool operationStarted = false)
        {
            Outcome = outcome;
            Payload = payload;
            Diagnostic = diagnostic;
            OperationStarted = operationStarted;
        }

        internal AutoRunOperationOutcome Outcome { get; }
        internal string Payload { get; }
        internal string Diagnostic { get; }
        internal bool OperationStarted { get; }
    }

    internal sealed class AutoRunHelperExecution
    {
        private AutoRunHelperExecution(bool succeeded, string payload, string diagnostic)
        {
            Succeeded = succeeded;
            Payload = payload;
            Diagnostic = diagnostic;
        }

        internal bool Succeeded { get; }
        internal string Payload { get; }
        internal string Diagnostic { get; }

        internal static AutoRunHelperExecution Success(string payload = null)
        {
            return new AutoRunHelperExecution(true, payload, null);
        }

        internal static AutoRunHelperExecution Failure(string diagnostic)
        {
            return new AutoRunHelperExecution(false, null, diagnostic);
        }
    }

    internal sealed class AutoRunOperationUncertainException : InvalidOperationException
    {
        internal AutoRunOperationUncertainException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Runs Task Scheduler operations in a bounded helper process. Task Scheduler's managed
    /// API does not support cancellation, so timing out an in-process mutation would allow it
    /// to complete after the settings transaction had started compensating. Terminating a
    /// dedicated process gives the caller a hard execution boundary.
    /// </summary>
    internal sealed class AutoRunOperationService
    {
        internal const string HelperSwitch = "--wiresock-autorun-helper";
        private const int HelperFailureExitCode = 10;
        private const int InvalidHelperArgumentsExitCode = 64;
        private const int MaximumHelperOutputCharacters = 128 * 1024;
        private const int MaximumHelperDiagnosticCharacters = 4096;
        private const int ProcessTerminationGraceMilliseconds = 2000;

        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        private readonly Func<string> _launcherPathProvider;
        private readonly Func<ProcessStartInfo, Process> _processStarter;
        private Process _unreapedProcess;
        private volatile bool _mutationStateUncertain;

        internal AutoRunOperationService(
            Func<string> launcherPathProvider,
            Func<ProcessStartInfo, Process> processStarter = null)
        {
            _launcherPathProvider =
                launcherPathProvider ?? throw new ArgumentNullException(nameof(launcherPathProvider));
            _processStarter = processStarter ?? Process.Start;
        }

        internal async Task<AutoRunOperationResult> ExecuteAsync(
            AutoRunHelperOperation operation,
            TimeSpan timeout,
            bool mutatesState,
            CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!TryReapPreviousProcess(out var unreapedDiagnostic))
                    return new AutoRunOperationResult(
                        AutoRunOperationOutcome.StateUncertain,
                        diagnostic: unreapedDiagnostic);

                if (mutatesState && _mutationStateUncertain)
                    return new AutoRunOperationResult(
                        AutoRunOperationOutcome.StateUncertain,
                        diagnostic:
                        "A previous autorun mutation timed out and its final state has not been verified.");

                return await ExecuteProcessAsync(
                        operation,
                        (int)timeout.TotalMilliseconds,
                        mutatesState,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        internal void AcknowledgeVerifiedMutationState()
        {
            _mutationStateUncertain = false;
        }

        private async Task<AutoRunOperationResult> ExecuteProcessAsync(
            AutoRunHelperOperation operation,
            int timeoutMilliseconds,
            bool mutatesState,
            CancellationToken cancellationToken)
        {
            var launcherPath = Path.GetFullPath(_launcherPathProvider() ?? string.Empty);
            if (!File.Exists(launcherPath))
                return new AutoRunOperationResult(
                    AutoRunOperationOutcome.Failed,
                    diagnostic: $"The trusted WireSock UI launcher '{launcherPath}' does not exist.");

            var startInfo = new ProcessStartInfo
            {
                FileName = launcherPath,
                Arguments = HelperSwitch + " " + GetOperationArgument(operation),
                WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ErrorDialog = false
            };

            Process process;
            try
            {
                process = _processStarter(startInfo);
            }
            catch (Exception ex)
            {
                return new AutoRunOperationResult(
                    AutoRunOperationOutcome.Failed,
                    diagnostic: $"The autorun helper could not be started: {ex.Message}");
            }

            if (process == null)
                return new AutoRunOperationResult(
                    AutoRunOperationOutcome.Failed,
                    diagnostic: "The autorun helper could not be started.");

            var outputTask = ReadBoundedOutputAsync(
                process.StandardOutput,
                MaximumHelperOutputCharacters);
            var errorTask = ReadBoundedOutputAsync(
                process.StandardError,
                MaximumHelperDiagnosticCharacters);
            try
            {
                var waitTask = Task.Run(() => process.WaitForExit(timeoutMilliseconds));
                var cancellationSignal = new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Task completedTask;
                using (cancellationToken.Register(
                           state => ((TaskCompletionSource<object>)state).TrySetResult(null),
                           cancellationSignal,
                           false))
                {
                    completedTask = await Task.WhenAny(waitTask, cancellationSignal.Task).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();

                var exited = ReferenceEquals(completedTask, waitTask) && await waitTask.ConfigureAwait(false);
                if (!exited)
                {
                    var terminated = TryTerminateAndConfirmExit(process, out var terminationDiagnostic);
                    ObserveStreamTask(outputTask);
                    ObserveStreamTask(errorTask);

                    if (!terminated)
                    {
                        _unreapedProcess = process;
                        if (mutatesState)
                            _mutationStateUncertain = true;
                        return new AutoRunOperationResult(
                            AutoRunOperationOutcome.StateUncertain,
                            diagnostic: terminationDiagnostic,
                            operationStarted: true);
                    }

                    process.Dispose();
                    if (mutatesState)
                    {
                        _mutationStateUncertain = true;
                        return new AutoRunOperationResult(
                            AutoRunOperationOutcome.StateUncertain,
                            diagnostic:
                            $"The autorun {GetOperationArgument(operation)} operation exceeded its " +
                            $"{timeoutMilliseconds} ms limit. The helper was terminated and the Task Scheduler state must be verified.",
                            operationStarted: true);
                    }

                    return new AutoRunOperationResult(
                        AutoRunOperationOutcome.TimedOut,
                        diagnostic:
                        $"The autorun {GetOperationArgument(operation)} operation exceeded its " +
                        $"{timeoutMilliseconds} ms limit.",
                        operationStarted: true);
                }

                // Complete the asynchronous pipe reads only after the process has closed its handles.
                var output = await outputTask.ConfigureAwait(false);
                var diagnostic = await errorTask.ConfigureAwait(false);
                var exitCode = process.ExitCode;
                process.Dispose();

                if (exitCode == 0)
                    return new AutoRunOperationResult(AutoRunOperationOutcome.Succeeded, output);

                var failureDiagnostic = string.IsNullOrWhiteSpace(diagnostic)
                    ? $"The autorun helper exited with code {exitCode}."
                    : diagnostic;
                if (!mutatesState)
                    return new AutoRunOperationResult(
                        AutoRunOperationOutcome.Failed,
                        diagnostic: failureDiagnostic,
                        operationStarted: true);

                // A helper failure does not prove that a Task Scheduler mutation was
                // atomic. Require an isolated inspection before permitting compensation
                // or a later mutation.
                _mutationStateUncertain = true;
                return new AutoRunOperationResult(
                    AutoRunOperationOutcome.StateUncertain,
                    diagnostic: failureDiagnostic,
                    operationStarted: true);
            }
            catch (OperationCanceledException)
            {
                var terminated = TryTerminateAndConfirmExit(process, out var terminationDiagnostic);
                ObserveStreamTask(outputTask);
                ObserveStreamTask(errorTask);
                if (terminated)
                {
                    process.Dispose();
                }
                else
                {
                    _unreapedProcess = process;
                }

                if (mutatesState)
                    _mutationStateUncertain = true;

                throw new OperationCanceledException(
                    terminated
                        ? "The autorun helper operation was cancelled and its process was terminated."
                        : terminationDiagnostic,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                var terminated = TryTerminateAndConfirmExit(process, out _);
                ObserveStreamTask(outputTask);
                ObserveStreamTask(errorTask);
                if (terminated)
                    process.Dispose();
                else
                    _unreapedProcess = process;
                if (mutatesState)
                    _mutationStateUncertain = true;

                return new AutoRunOperationResult(
                    AutoRunOperationOutcome.StateUncertain,
                    diagnostic: $"The autorun helper could not be monitored safely: {ex.Message}",
                    operationStarted: true);
            }
        }

        private bool TryReapPreviousProcess(out string diagnostic)
        {
            diagnostic = null;
            if (_unreapedProcess == null)
                return true;

            try
            {
                if (!_unreapedProcess.HasExited)
                {
                    diagnostic =
                        "A previous autorun helper process has not exited. No additional autorun operation will start.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                diagnostic = $"The previous autorun helper process state is unknown: {ex.Message}";
                return false;
            }

            _unreapedProcess.Dispose();
            _unreapedProcess = null;
            return true;
        }

        private static bool TryTerminateAndConfirmExit(Process process, out string diagnostic)
        {
            diagnostic = null;
            try
            {
                if (process.HasExited)
                    return true;

                process.Kill();
                if (process.WaitForExit(ProcessTerminationGraceMilliseconds))
                    return true;

                diagnostic =
                    "The timed-out autorun helper could not be confirmed terminated. " +
                    "No additional autorun operation will start in this process.";
                return false;
            }
            catch (InvalidOperationException)
            {
                // The process exited between HasExited and Kill.
                return true;
            }
            catch (Exception ex)
            {
                diagnostic =
                    $"The timed-out autorun helper could not be terminated safely: {ex.Message}";
                return false;
            }
        }

        private static void ObserveStreamTask(Task<string> task)
        {
            task.ContinueWith(
                completed => Trace.TraceWarning(
                    $"The autorun helper output stream failed after termination: {completed.Exception?.GetBaseException()}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static async Task<string> ReadBoundedOutputAsync(
            StreamReader reader,
            int maximumCharacters)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (maximumCharacters < 4)
                throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

            var builder = new StringBuilder(Math.Min(maximumCharacters, 4096));
            var buffer = new char[4096];
            var truncated = false;
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                var retained = Math.Min(read, maximumCharacters - builder.Length);
                if (retained > 0)
                    builder.Append(buffer, 0, retained);
                if (retained < read)
                    truncated = true;
            }

            if (!truncated)
                return builder.ToString();

            var retainedLength = maximumCharacters - 3;
            if (retainedLength > 0 &&
                retainedLength < builder.Length &&
                char.IsHighSurrogate(builder[retainedLength - 1]) &&
                char.IsLowSurrogate(builder[retainedLength]))
                retainedLength--;
            builder.Length = Math.Min(builder.Length, retainedLength);
            builder.Append("...");
            return builder.ToString();
        }

        private static string BoundOutput(string value, int maximumCharacters)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximumCharacters)
                return value;

            return value.Substring(0, maximumCharacters - 3) + "...";
        }

        private static string GetOperationArgument(AutoRunHelperOperation operation)
        {
            switch (operation)
            {
                case AutoRunHelperOperation.Inspect:
                    return "inspect";
                case AutoRunHelperOperation.Enable:
                    return "enable";
                case AutoRunHelperOperation.EnableMigratingLegacyTask:
                    return "enable-migrating-legacy-task";
                case AutoRunHelperOperation.Disable:
                    return "disable";
                case AutoRunHelperOperation.DisableMigratingLegacyTask:
                    return "disable-migrating-legacy-task";
                case AutoRunHelperOperation.DeleteLegacyShortcut:
                    return "delete-legacy-shortcut";
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        private static bool TryParseOperation(string value, out AutoRunHelperOperation operation)
        {
            switch (value)
            {
                case "inspect":
                    operation = AutoRunHelperOperation.Inspect;
                    return true;
                case "enable":
                    operation = AutoRunHelperOperation.Enable;
                    return true;
                case "enable-migrating-legacy-task":
                    operation = AutoRunHelperOperation.EnableMigratingLegacyTask;
                    return true;
                case "disable":
                    operation = AutoRunHelperOperation.Disable;
                    return true;
                case "disable-migrating-legacy-task":
                    operation = AutoRunHelperOperation.DisableMigratingLegacyTask;
                    return true;
                case "delete-legacy-shortcut":
                    operation = AutoRunHelperOperation.DeleteLegacyShortcut;
                    return true;
                default:
                    operation = default(AutoRunHelperOperation);
                    return false;
            }
        }

        internal static bool TryRunHelperCommandLine(
            string[] arguments,
            Func<AutoRunHelperOperation, AutoRunHelperExecution> execute,
            out int exitCode)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));

            exitCode = 0;
            if (arguments == null || arguments.Length < 2 ||
                !string.Equals(arguments[1], HelperSwitch, StringComparison.Ordinal))
                return false;

            if (arguments.Length != 3 || !TryParseOperation(arguments[2], out var operation))
            {
                Console.Error.Write("Invalid WireSock UI autorun helper arguments.");
                exitCode = InvalidHelperArgumentsExitCode;
                return true;
            }

            AutoRunHelperExecution result;
            try
            {
                result = execute(operation) ??
                         AutoRunHelperExecution.Failure(
                             "The autorun helper operation completed without a result.");
            }
            catch (Exception ex)
            {
                result = AutoRunHelperExecution.Failure(ex.Message);
            }

            if (result.Succeeded)
            {
                Console.Out.Write(BoundOutput(result.Payload, MaximumHelperOutputCharacters));
                exitCode = 0;
            }
            else
            {
                Console.Error.Write(BoundOutput(
                    result.Diagnostic ?? "The autorun helper operation failed.",
                    MaximumHelperDiagnosticCharacters));
                exitCode = HelperFailureExitCode;
            }

            return true;
        }
    }
}
