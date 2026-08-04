using System.Text;

namespace ServiceKit.Net
{
    // Thrown by WorkflowSaga.CompensateAsync when at least one compensation failed, so the rollback
    // is incomplete and needs a human. The InnerException is the ORIGINAL business failure that
    // triggered the rollback - without that the compensation error would bury the actual cause.
    //
    // Register this type in TemporalWorkerOptions.WorkflowFailureExceptionTypes, otherwise Temporal
    // fails the workflow TASK instead of the workflow, and retries it forever.
    public sealed class WorkflowCompensationException : Exception
    {
        public WorkflowCompensationException(IReadOnlyList<WorkflowCompensationFailure> failures, Exception originalFailure)
            : base(_BuildMessage(failures), originalFailure)
        {
            Failures = failures ?? new List<WorkflowCompensationFailure>();
        }

        // Every compensation that failed, in the order they were attempted (reverse of the forward steps)
        public IReadOnlyList<WorkflowCompensationFailure> Failures { get; }

        private static string _BuildMessage(IReadOnlyList<WorkflowCompensationFailure> failures)
        {
            if (failures == null || failures.Count == 0)
                return "workflow compensation failed";

            var message = new StringBuilder();
            message.Append("workflow compensation failed for ");
            message.Append(failures.Count);
            message.Append(failures.Count == 1 ? " step: " : " steps: ");

            for (int i = 0; i < failures.Count; i++)
            {
                if (i > 0)
                    message.Append(", ");

                message.Append(failures[i].StepName);
                message.Append(" (");
                message.Append(failures[i].Failure?.GetType().Name);
                message.Append(": ");
                message.Append(failures[i].Failure?.Message);
                message.Append(')');
            }

            return message.ToString();
        }
    }
}
