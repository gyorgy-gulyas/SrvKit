namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// What travels: the fact, plus everything needed to deliver it, recognise it again and find it
    /// in a trace.
    ///
    /// The envelope is deliberately separate from the payload. A published event is a contract with
    /// somebody else, and putting delivery plumbing - attempt counts, correlation ids - into that
    /// contract would mean every consumer's generated type carries fields that have nothing to do
    /// with the business fact. So the contract stays clean and this carries the rest.
    /// </summary>
    public sealed class EventEnvelope
    {
        /// <summary>
        /// Identity of this delivery. The consumer side keeps seen ids so a redelivery can be
        /// dropped - which is the only reason at-least-once is a workable promise.
        /// </summary>
        public string EventId { get; set; }

        /// <summary>See <see cref="IDomainEvent.SchemaId"/>. This is what dispatch matches on.</summary>
        public string SchemaId { get; set; }

        /// <summary>See <see cref="IDomainEvent.Channel"/>.</summary>
        public string Channel { get; set; }

        /// <summary>
        /// The ordering scope. Facts sharing a partition key arrive in the order they were recorded;
        /// between different keys there is no order and none is promised. For a fact recorded by an
        /// aggregate root this is the root's identity.
        /// </summary>
        public string PartitionKey { get; set; }

        /// <summary>When the fact happened - not when it was delivered.</summary>
        public DateTimeOffset OccurredAt { get; set; }

        /// <summary>
        /// The call this fact belongs to. It is the SAME value as the trace id: two identifiers for
        /// one call means somebody has to pair them up by hand.
        /// </summary>
        public string CorrelationId { get; set; }

        /// <summary>
        /// What caused this fact - the id of the event being handled when it was recorded, if any.
        /// This is how a chain of consequences can be walked backwards.
        /// </summary>
        public string CausationId { get; set; }

        /// <summary>The tenant the fact belongs to, when the deployment is multi-tenant.</summary>
        public string TenantId { get; set; }

        /// <summary>The context that recorded it. Useful in a trace; never used for routing.</summary>
        public string Source { get; set; }

        /// <summary>The serialized fact.</summary>
        public string Payload { get; set; }

        /// <summary>How <see cref="Payload"/> is encoded. Defaults to JSON.</summary>
        public string ContentType { get; set; } = "application/json";

        /// <summary>
        /// How many times delivery has been attempted. Set by the delivery pipeline, not by the
        /// producer - a producer records once and is done.
        /// </summary>
        public int Attempt { get; set; }
    }
}
