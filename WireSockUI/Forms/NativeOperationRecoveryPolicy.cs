namespace WireSockUI.Forms
{
    internal static class NativeOperationRecoveryPolicy
    {
        internal static bool CanRestorePreviousState<T>(NativeOperationResult<T> completedResult)
        {
            return completedResult?.Succeeded == true;
        }

        internal static bool MustDeferCleanup<T>(NativeOperationResult<T> operationResult)
        {
            return operationResult?.TimedOut == true;
        }
    }
}
