namespace ServiceKit.Net
{
    // Records the compensation of every completed workflow step and rolls them back in reverse order.
    //
    // Deliberately free of any Temporal dependency: it is unit testable without a running server, and
    // it is safe inside a workflow context - no locking, no clock, no thread hops, and no
    // ConfigureAwait(false) (that would move the continuation off Temporal's deterministic scheduler).
    //
    // The generated step facade pushes here after each successful step; the generated workflow run
    // wrapper calls CompensateAsync on failure. That is the whole saga guarantee: the developer writes
    // the order of the steps, the generated code guarantees the rollback.
    public sealed class WorkflowSaga
    {
        private readonly List<_Entry> _pending = new List<_Entry>();
        private bool _compensated = false;

        // Compensations recorded and not yet rolled back
        public int PendingCount => _pending.Count;

        // The step names still on the stack, in the order they were pushed
        public IReadOnlyList<string> PendingSteps => _pending.Select(entry => entry.StepName).ToList();

        // True once CompensateAsync has run - the saga is spent, Push is no longer accepted
        public bool HasCompensated => _compensated;

        // Record the rollback of a step that has just succeeded.
        // The step name is mandatory: without it the rollback error message is unusable, and the
        // generator always knows it (nameof).
        public void Push(string stepName, Func<Task> compensation)
        {
            if (string.IsNullOrWhiteSpace(stepName) == true)
                throw new ArgumentException("the compensated step name is required", nameof(stepName));

            if (compensation == null)
                throw new ArgumentNullException(nameof(compensation));

            if (_compensated == true)
                throw new InvalidOperationException($"cannot record a compensation for step '{stepName}', the saga has already been compensated");

            _pending.Add(new _Entry(stepName, compensation));
        }

        // Roll everything back without an originating exception (rarely what you want - prefer the
        // overload that carries the failure, so the cause survives).
        public Task CompensateAsync()
        {
            return CompensateAsync(null);
        }

        // Roll every recorded step back in reverse order.
        //
        // A failing compensation does NOT stop the others: if it did, the steps before it would stay
        // rolled forward. Every failure is collected and reported together in a
        // WorkflowCompensationException whose InnerException is the original failure.
        //
        // Calling it twice is a no-op - the second call must not run the compensations again.
        public async Task CompensateAsync(Exception originalFailure)
        {
            if (_compensated == true)
                return;

            _compensated = true;

            List<WorkflowCompensationFailure> failures = null;

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var entry = _pending[i];

                try
                {
                    await entry.Compensation();
                }
                catch (Exception ex)
                {
                    if (failures == null)
                        failures = new List<WorkflowCompensationFailure>();

                    failures.Add(new WorkflowCompensationFailure(entry.StepName, ex));
                }
            }

            _pending.Clear();

            if (failures != null)
                throw new WorkflowCompensationException(failures, originalFailure);
        }

        private sealed class _Entry
        {
            public _Entry(string stepName, Func<Task> compensation)
            {
                StepName = stepName;
                Compensation = compensation;
            }

            public string StepName { get; }
            public Func<Task> Compensation { get; }
        }
    }
}
