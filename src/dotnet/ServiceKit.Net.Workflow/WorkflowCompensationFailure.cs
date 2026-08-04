namespace ServiceKit.Net
{
    // One failed rollback: which step's compensation blew up, and with what.
    // Collected by WorkflowSaga so that a single bad compensation does not hide the others.
    public sealed class WorkflowCompensationFailure
    {
        public WorkflowCompensationFailure(string stepName, Exception failure)
        {
            StepName = stepName;
            Failure = failure;
        }

        // Name of the forward step whose compensation failed
        public string StepName { get; }

        // The exception the compensation threw
        public Exception Failure { get; }
    }
}
