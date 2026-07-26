using System;
using System.Threading.Tasks;

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

        internal static async Task<NativeOperationResult<T>> AwaitTimedOutCompletionAsync<T>(
            NativeOperationResult<T> operationResult, string context)
        {
            if (operationResult == null)
                return NormalizeCompletion<T>(null, context);
            if (!operationResult.TimedOut)
                return operationResult;
            if (operationResult.PendingCompletion == null)
                return NativeOperationResult<T>.Failure(
                    $"The timed-out {context ?? "native operation"} did not provide a completion task.");

            try
            {
                return NormalizeCompletion(
                    await operationResult.PendingCompletion.ConfigureAwait(false), context);
            }
            catch (Exception ex)
            {
                return NativeOperationResult<T>.Failure(ex.Message);
            }
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
