namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// One handler bound to one schema id.
    /// </summary>
    public sealed class EventSubscription
    {
        public string SchemaId { get; init; }
        public string Channel { get; init; }
        public Type EventType { get; init; }
        public Type HandlerType { get; init; }
    }

    /// <summary>
    /// Who listens to what, in this process.
    ///
    /// Filled at registration - by generated code, not by hand. Dispatch matches on the schema id
    /// alone, which is why a subscriber never needs to reference the producing service's contract:
    /// it needs the event type and nothing else.
    /// </summary>
    public sealed class EventSubscriptionRegistry
    {
        private readonly List<EventSubscription> _subscriptions = new();

        public IReadOnlyList<EventSubscription> Subscriptions => _subscriptions;

        /// <summary>Every distinct channel something in this process listens to.</summary>
        public IReadOnlyList<string> Channels =>
            _subscriptions.Select(s => s.Channel).Distinct(StringComparer.Ordinal).ToArray();

        public IReadOnlyList<EventSubscription> For(string schemaId) =>
            _subscriptions.Where(s => string.Equals(s.SchemaId, schemaId, StringComparison.Ordinal)).ToArray();

        public void Add(Type eventType, Type handlerType)
        {
            if (typeof(IDomainEvent).IsAssignableFrom(eventType) == false)
                throw new ArgumentException($"'{eventType.FullName}' is not an event: it does not implement IDomainEvent.", nameof(eventType));

            // The schema id and channel are instance members because a generated event carries them
            // as ordinary properties - so one throwaway instance is created here, once, at
            // registration, to read them. Cheap, and it keeps the generated shape simple.
            var probe = (IDomainEvent)Activator.CreateInstance(eventType);

            _subscriptions.Add(new EventSubscription()
            {
                SchemaId = probe.SchemaId,
                Channel = probe.Channel,
                EventType = eventType,
                HandlerType = handlerType,
            });
        }
    }
}
