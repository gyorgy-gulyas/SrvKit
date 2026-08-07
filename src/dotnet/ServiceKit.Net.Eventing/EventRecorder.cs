using System.Diagnostics;

namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// The default recorder: it fills in the envelope from the ambient call and keeps the result in
    /// memory until the repository drains it.
    ///
    /// It is scoped to a unit of work, not shared - two concurrent requests recording into the same
    /// list would hand each other's facts to whichever saved first.
    /// </summary>
    public sealed class EventRecorder : IEventRecorder
    {
        private readonly List<EventEnvelope> _pending = new();
        private readonly IEventSerializer _serializer;
        private readonly EventRecordingContext _context;

        public EventRecorder(IEventSerializer serializer, EventRecordingContext context = null)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _context = context ?? new EventRecordingContext();
        }

        public bool HasPending => _pending.Count > 0;

        public void Record(IDomainEvent @event, string partitionKey)
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            // A fact without an ordering scope cannot be ordered against anything, and silently
            // giving it an arbitrary one would hide that. Say so instead.
            if (string.IsNullOrWhiteSpace(partitionKey) == true)
                throw new ArgumentException($"The event '{@event.SchemaId}' was recorded without a partition key. The partition key is the ordering scope - for a fact recorded by an aggregate root it is the root's identity.", nameof(partitionKey));

            _pending.Add(new EventEnvelope()
            {
                EventId = Guid.NewGuid().ToString("D"),
                SchemaId = @event.SchemaId,
                Channel = @event.Channel,
                PartitionKey = partitionKey,
                OccurredAt = DateTimeOffset.UtcNow,
                // The trace id IS the correlation id when the caller did not bring one of its own.
                CorrelationId = string.IsNullOrWhiteSpace(_context.CorrelationId) == false
                    ? _context.CorrelationId
                    : Activity.Current?.TraceId.ToString(),
                CausationId = _context.CausationId,
                TenantId = _context.TenantId,
                Source = _context.Source,
                Payload = _serializer.Serialize(@event),
                ContentType = _serializer.ContentType,
            });
        }

        public IReadOnlyList<EventEnvelope> Drain()
        {
            var drained = _pending.ToArray();
            _pending.Clear();
            return drained;
        }
    }

    /// <summary>
    /// What the ambient call contributes to every envelope recorded during it.
    ///
    /// Filled by the host from the CallingContext, and by the dispatcher when a handler records
    /// facts of its own - that is where CausationId comes from, and it is what turns a pile of
    /// events into a chain that can be walked backwards.
    /// </summary>
    public sealed class EventRecordingContext
    {
        public string CorrelationId { get; set; }
        public string CausationId { get; set; }
        public string TenantId { get; set; }
        public string Source { get; set; }
    }
}
