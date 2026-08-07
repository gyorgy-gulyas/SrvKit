namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// The knobs that belong to a deployment, not to a model.
    ///
    /// None of these ever appear in a .d3 file. The model says which fact exists and who reacts to
    /// it; how often the relay looks, how many times a failure is retried and what the consumer
    /// group is called are operational decisions.
    /// </summary>
    public sealed class EventingOptions
    {
        /// <summary>
        /// Who this process is, as a listener. Two instances of the same service share a group and
        /// therefore share the work; a different service is a different group and gets its own copy.
        ///
        /// Defaults to the entry assembly's name, which is right often enough to be a useful default
        /// and obvious enough to override when it is not.
        /// </summary>
        public string ConsumerGroup { get; set; } = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "servicekit";

        /// <summary>How many outbox rows the relay takes per pass.</summary>
        public int RelayBatchSize { get; set; } = 100;

        /// <summary>How long the relay waits after finding nothing.</summary>
        public TimeSpan RelayIdleDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// How many times a handler may fail before the envelope is dead-lettered.
        ///
        /// It counts ALL attempts, so 1 means "do not retry" - the same reading as @retry on a
        /// workflow, because two counters in one platform that count differently is a trap.
        /// </summary>
        public int MaxDeliveryAttempts { get; set; } = 5;

        /// <summary>
        /// Turns the relay off in this process.
        ///
        /// A deployment may prefer to run the relay somewhere else - one instance draining the
        /// outbox instead of every replica polling it. The recording side is unaffected either way,
        /// which is the point of writing the intent down first.
        /// </summary>
        public bool RunRelay { get; set; } = true;
    }
}
