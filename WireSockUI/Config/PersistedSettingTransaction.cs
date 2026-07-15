using System;
using System.Threading.Tasks;

namespace WireSockUI.Config
{
    internal enum PersistedSettingUpdateStatus
    {
        Succeeded,
        InitialSaveFailed,
        RuntimeApplyFailed,
        RollbackSaveFailed
    }

    internal sealed class PersistedSettingUpdateResult
    {
        internal PersistedSettingUpdateResult(
            PersistedSettingUpdateStatus status,
            Exception exception = null)
        {
            Status = status;
            Exception = exception;
        }

        internal PersistedSettingUpdateStatus Status { get; }
        internal Exception Exception { get; }
    }

    internal static class PersistedSettingTransaction
    {
        internal static PersistedSettingUpdateResult Apply<T>(
            T requestedValue,
            T previousValue,
            Action<T> setValue,
            Action save,
            Func<bool> applyRuntimeState)
        {
            if (setValue == null) throw new ArgumentNullException(nameof(setValue));
            if (save == null) throw new ArgumentNullException(nameof(save));
            if (applyRuntimeState == null) throw new ArgumentNullException(nameof(applyRuntimeState));

            setValue(requestedValue);
            try
            {
                save();
            }
            catch (Exception ex)
            {
                setValue(previousValue);
                return new PersistedSettingUpdateResult(PersistedSettingUpdateStatus.InitialSaveFailed, ex);
            }

            Exception runtimeException = null;
            var runtimeApplied = false;
            try
            {
                runtimeApplied = applyRuntimeState();
            }
            catch (Exception ex)
            {
                runtimeException = ex;
            }

            if (runtimeApplied)
                return new PersistedSettingUpdateResult(PersistedSettingUpdateStatus.Succeeded);

            setValue(previousValue);
            try
            {
                save();
            }
            catch (Exception ex)
            {
                // The requested value was the last value known to have been saved successfully.
                setValue(requestedValue);
                return new PersistedSettingUpdateResult(PersistedSettingUpdateStatus.RollbackSaveFailed, ex);
            }

            return new PersistedSettingUpdateResult(
                PersistedSettingUpdateStatus.RuntimeApplyFailed,
                runtimeException);
        }

        internal static async Task<PersistedSettingUpdateResult> ApplyAsync<T>(
            T requestedValue,
            T previousValue,
            Action<T> setValue,
            Action save,
            Func<Task<bool>> applyRuntimeState)
        {
            if (setValue == null) throw new ArgumentNullException(nameof(setValue));
            if (save == null) throw new ArgumentNullException(nameof(save));
            if (applyRuntimeState == null) throw new ArgumentNullException(nameof(applyRuntimeState));

            setValue(requestedValue);
            try
            {
                save();
            }
            catch (Exception ex)
            {
                setValue(previousValue);
                return new PersistedSettingUpdateResult(PersistedSettingUpdateStatus.InitialSaveFailed, ex);
            }

            Exception runtimeException = null;
            var runtimeApplied = false;
            try
            {
                runtimeApplied = await applyRuntimeState();
            }
            catch (Exception ex)
            {
                runtimeException = ex;
            }

            if (runtimeApplied)
                return new PersistedSettingUpdateResult(PersistedSettingUpdateStatus.Succeeded);

            setValue(previousValue);
            try
            {
                save();
            }
            catch (Exception ex)
            {
                setValue(requestedValue);
                return new PersistedSettingUpdateResult(PersistedSettingUpdateStatus.RollbackSaveFailed, ex);
            }

            return new PersistedSettingUpdateResult(
                PersistedSettingUpdateStatus.RuntimeApplyFailed,
                runtimeException);
        }
    }
}
