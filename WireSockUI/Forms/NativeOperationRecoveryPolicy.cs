namespace WireSockUI.Forms
{
    internal static class NativeOperationRecoveryPolicy
    {
        internal static NativeOperationResult<T> NormalizeCompletion<T>(NativeOperationResult<T> completedResult,
            string context)
        {
            if (completedResult != null)
                return completedResult;

            var operation = string.IsNullOrWhiteSpace(context) ? "native operation" : context;
            return NativeOperationResult<T>.Failure(
                $"The timed-out {operation} completed without a result.");
        }

        internal static bool CanRestorePreviousState<T>(NativeOperationResult<T> completedResult)
        {
            return completedResult?.Succeeded == true;
        }

        internal static bool MustDeferCleanup<T>(NativeOperationResult<T> operationResult)
        {
            return operationResult?.TimedOut == true;
        }

        internal static string AppendDiagnostic(string existingDiagnostic, string additionalDiagnostic)
        {
            if (string.IsNullOrWhiteSpace(existingDiagnostic))
                return string.IsNullOrWhiteSpace(additionalDiagnostic) ? null : additionalDiagnostic;
            if (string.IsNullOrWhiteSpace(additionalDiagnostic))
                return existingDiagnostic;

            return existingDiagnostic + " " + additionalDiagnostic;
        }
    }
}
