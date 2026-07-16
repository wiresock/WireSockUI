using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WireSockUI.Config
{
    internal sealed class CompensatingTransactionStep
    {
        internal CompensatingTransactionStep(string name, Func<Task<bool>> apply, Func<Task<bool>> rollback)
        {
            Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A step name is required.", nameof(name)) : name;
            Apply = apply ?? throw new ArgumentNullException(nameof(apply));
            Rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        }

        internal string Name { get; }
        internal Func<Task<bool>> Apply { get; }
        internal Func<Task<bool>> Rollback { get; }
    }

    internal sealed class CompensatingTransactionResult
    {
        internal CompensatingTransactionResult(bool succeeded, string failedStep = null,
            Exception exception = null, IReadOnlyList<string> rollbackFailures = null)
        {
            Succeeded = succeeded;
            FailedStep = failedStep;
            Exception = exception;
            RollbackFailures = rollbackFailures ?? Array.Empty<string>();
        }

        internal bool Succeeded { get; }
        internal string FailedStep { get; }
        internal Exception Exception { get; }
        internal IReadOnlyList<string> RollbackFailures { get; }

        internal bool RollbackFailed(string stepName)
        {
            if (string.IsNullOrWhiteSpace(stepName))
                throw new ArgumentException("A step name is required.", nameof(stepName));

            return RollbackFailures.Any(failure =>
                string.Equals(failure, stepName, StringComparison.Ordinal) ||
                failure.StartsWith(stepName + ":", StringComparison.Ordinal));
        }
    }

    internal static class CompensatingTransaction
    {
        internal static async Task<CompensatingTransactionResult> ApplyAsync(
            IReadOnlyList<CompensatingTransactionStep> steps)
        {
            if (steps == null) throw new ArgumentNullException(nameof(steps));
            if (steps.Any(step => step == null)) throw new ArgumentException("Transaction steps cannot be null.", nameof(steps));

            var applied = new List<CompensatingTransactionStep>();
            foreach (var step in steps)
            {
                Exception applyException = null;
                var succeeded = false;
                try
                {
                    succeeded = await step.Apply();
                }
                catch (Exception ex)
                {
                    applyException = ex;
                }

                if (succeeded)
                {
                    applied.Add(step);
                    continue;
                }

                var rollbackFailures = await RollBackAsync(step, applied);
                return new CompensatingTransactionResult(false, step.Name, applyException, rollbackFailures);
            }

            return new CompensatingTransactionResult(true);
        }

        private static async Task<IReadOnlyList<string>> RollBackAsync(CompensatingTransactionStep failedStep,
            IReadOnlyList<CompensatingTransactionStep> applied)
        {
            var rollbackFailures = new List<string>();
            var stepsToRollback = new[] { failedStep }.Concat(applied.Reverse());

            foreach (var step in stepsToRollback)
            {
                try
                {
                    if (!await step.Rollback())
                        rollbackFailures.Add(step.Name);
                }
                catch (Exception ex)
                {
                    rollbackFailures.Add($"{step.Name}: {ex.Message}");
                }
            }

            return rollbackFailures;
        }
    }
}
