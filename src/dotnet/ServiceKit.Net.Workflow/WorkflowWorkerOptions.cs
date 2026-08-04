using Temporalio.Worker;

namespace ServiceKit.Net
{
    // Process level settings for WorkflowWorkerHost. One host serves every registered queue, so these
    // apply to all of them; per queue tuning goes through ConfigureWorker.
    public sealed class WorkflowWorkerOptions
    {
        // Temporal frontend address, used only when no ITemporalClient is registered in the container
        public string TargetHost { get; set; } = "localhost:7233";

        // Temporal namespace, used only when no ITemporalClient is registered in the container
        public string Namespace { get; set; } = "default";

        // How long a worker may finish its running tasks on shutdown
        public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

        // Exceptions that fail the WORKFLOW rather than the workflow task. WorkflowCompensationException
        // is seeded here on purpose: leave it out and a failed rollback fails the workflow task instead,
        // which Temporal then retries forever.
        public IList<Type> WorkflowFailureExceptionTypes { get; } = new List<Type> { typeof(WorkflowCompensationException) };

        // Last word on the worker options of every queue, applied after everything else
        public Action<TemporalWorkerOptions> ConfigureWorker { get; set; }
    }
}
