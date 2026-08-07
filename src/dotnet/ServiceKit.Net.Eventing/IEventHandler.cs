namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// What the delivery pipeline tells a handler about the delivery itself.
    ///
    /// Separate from the event because none of it belongs to the business fact - and because a
    /// handler that needs to be idempotent needs the delivery identity, not the payload.
    /// </summary>
    public sealed class EventContext
    {
        public string EventId { get; init; }
        public string SchemaId { get; init; }
        public string PartitionKey { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public string CorrelationId { get; init; }
        public string CausationId { get; init; }
        public string TenantId { get; init; }
        public string Source { get; init; }

        /// <summary>
        /// Which attempt this is, starting at 1. A handler is allowed to behave differently on a
        /// retry - logging louder, say - but it must not become correct only on the first attempt.
        /// </summary>
        public int Attempt { get; init; }

        public static EventContext From(EventEnvelope envelope)
        {
            return new EventContext()
            {
                EventId = envelope.EventId,
                SchemaId = envelope.SchemaId,
                PartitionKey = envelope.PartitionKey,
                OccurredAt = envelope.OccurredAt,
                CorrelationId = envelope.CorrelationId,
                CausationId = envelope.CausationId,
                TenantId = envelope.TenantId,
                Source = envelope.Source,
                Attempt = envelope.Attempt,
            };
        }
    }

    /// <summary>
    /// The consuming half of the model's `eventhandler`.
    ///
    /// The generated code implements this and registers it automatically; the developer writes only
    /// the body. Automatic registration is not a nicety - a generated surface that has to be wired
    /// up by hand is a surface that silently never runs, and this platform has already shipped that
    /// bug twice.
    /// </summary>
    public interface IEventHandler<in TEvent> where TEvent : IDomainEvent
    {
        Task Handle(EventContext context, TEvent @event, CancellationToken cancellationToken = default);
    }
}
