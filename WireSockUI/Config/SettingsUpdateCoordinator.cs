using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WireSockUI.Config
{
    internal sealed class SettingsUpdateCoordinator
    {
        internal const string NativeKillSwitchStepName = "native Kill Switch";
        private readonly Func<bool, bool, Task<bool>> _applyKillSwitch;
        private readonly Func<string, Task<bool>> _applyLogLevel;
        private readonly Func<ApplicationSettingsSnapshot, Task<bool>> _persistSettings;

        internal SettingsUpdateCoordinator(Func<string, Task<bool>> applyLogLevel,
            Func<bool, bool, Task<bool>> applyKillSwitch,
            Func<ApplicationSettingsSnapshot, Task<bool>> persistSettings = null)
        {
            _applyLogLevel = applyLogLevel ?? throw new ArgumentNullException(nameof(applyLogLevel));
            _applyKillSwitch = applyKillSwitch ?? throw new ArgumentNullException(nameof(applyKillSwitch));
            _persistSettings = persistSettings ?? PersistSettingsAsync;
        }

        internal Task<CompensatingTransactionResult> ApplyAsync(
            ApplicationSettingsSnapshot previousSettings,
            ApplicationSettingsSnapshot requestedSettings,
            bool hasTunnelHandle,
            Func<Task<bool>> applyAutoRun,
            Func<Task<bool>> rollbackAutoRun)
        {
            if (previousSettings == null) throw new ArgumentNullException(nameof(previousSettings));
            if (requestedSettings == null) throw new ArgumentNullException(nameof(requestedSettings));
            if (applyAutoRun == null) throw new ArgumentNullException(nameof(applyAutoRun));
            if (rollbackAutoRun == null) throw new ArgumentNullException(nameof(rollbackAutoRun));

            var logLevelChanged = !string.Equals(requestedSettings.LogLevel,
                previousSettings.LogLevel, StringComparison.Ordinal);
            var killSwitchRequiresNativeUpdate =
                requestedSettings.EnableKillSwitch != previousSettings.EnableKillSwitch || hasTunnelHandle;

            return CompensatingTransaction.ApplyAsync(new List<CompensatingTransactionStep>
            {
                new CompensatingTransactionStep("autorun task", applyAutoRun, rollbackAutoRun),
                new CompensatingTransactionStep(
                    "native log level",
                    () => logLevelChanged
                        ? _applyLogLevel(requestedSettings.LogLevel)
                        : Task.FromResult(true),
                    () => logLevelChanged
                        ? _applyLogLevel(previousSettings.LogLevel)
                        : Task.FromResult(true)),
                new CompensatingTransactionStep(
                    "settings persistence",
                    () => _persistSettings(requestedSettings),
                    () => _persistSettings(previousSettings)),
                new CompensatingTransactionStep(
                    NativeKillSwitchStepName,
                    () => _applyKillSwitch(requestedSettings.EnableKillSwitch,
                        killSwitchRequiresNativeUpdate),
                    () => _applyKillSwitch(previousSettings.EnableKillSwitch,
                        killSwitchRequiresNativeUpdate))
            });
        }

        internal static bool FailureRequiresNativeRecovery(CompensatingTransactionResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            return result.RollbackFailed(NativeKillSwitchStepName);
        }

        private static Task<bool> PersistSettingsAsync(ApplicationSettingsSnapshot settings)
        {
            settings.Persist();
            return Task.FromResult(true);
        }
    }
}
