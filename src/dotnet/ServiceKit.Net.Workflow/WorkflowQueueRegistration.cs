namespace ServiceKit.Net
{
    // Everything one Temporal task queue needs: the workflows served on it, and the activity
    // interfaces whose implementations must be resolved from the container for it.
    public sealed class WorkflowQueueRegistration
    {
        public WorkflowQueueRegistration(string taskQueue)
        {
            if (string.IsNullOrWhiteSpace(taskQueue) == true)
                throw new ArgumentException("the task queue name is required", nameof(taskQueue));

            TaskQueue = taskQueue;
        }

        // Derived per workflow by the generator as "<context>.<workflow>", overridable in the
        // generated registration
        public string TaskQueue { get; }

        // Generated [Workflow] classes served on this queue
        public IList<Type> WorkflowTypes { get; } = new List<Type>();

        // Generated activity INTERFACES (I<Name>Activities); the implementations come from the container
        public IList<Type> ActivityServiceTypes { get; } = new List<Type>();
    }
}
