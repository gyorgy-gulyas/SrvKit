using PolyPersist;
using PolyPersist.Net.Core;

namespace ServiceKit.Net.Eventing.PolyPersistStores
{
    /// <summary>
    /// A recorded fact waiting to leave, as a stored document.
    ///
    /// The envelope is flattened rather than kept as one serialized blob, so an operator can look at
    /// the outbox and see what is stuck and why without deserializing anything. Only the payload -
    /// the fact itself - stays opaque, which is also the only part that may contain personal data.
    /// </summary>
    public sealed class OutboxRecord : Entity, IDocument
    {
        // id, etag, PartitionKey and LastUpdate come from Entity. The envelope's EventId becomes the
        // document id, which is what makes a repeated write of the same fact a duplicate rather than
        // a second row.

        /// <summary>
        /// The SHARD, not the event's ordering key.
        ///
        /// The relay drains everything, and a partition-scoped read of one shard beats a
        /// cross-partition scan of the whole outbox on every backend that has partitions at all.
        /// Because the shard is derived from the event's partition key, every fact of one aggregate
        /// lands in the same shard - so ordering within a key survives, which is the only ordering
        /// the platform promises. Scaling out means more shards and more relay workers.
        ///
        /// (This is what Entity.PartitionKey holds on this record - see the note above.)
        /// </summary>

        /// <summary>
        /// The drain order, within a shard.
        ///
        /// Across processes this is only as good as the clock - but two writers to the SAME
        /// aggregate cannot both succeed, because the aggregate's own optimistic concurrency stops
        /// one of them. So skew can only reorder unrelated keys, and between unrelated keys no order
        /// was ever promised.
        /// </summary>
        public long RecordedAtTicks { get; set; }

        public bool Sent { get; set; }

        /// <summary>Visible in the store, so a stuck fact is something an operator can see rather than infer.</summary>
        public int FailedAttempts { get; set; }
        public string LastFailure { get; set; }

        // --- the envelope, flattened ---------------------------------------------------------
        public string SchemaId { get; set; }
        public string Channel { get; set; }
        /// <summary>The event's own ordering key - what <see cref="PartitionKey"/> is derived FROM.</summary>
        public string EventPartitionKey { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        public string CorrelationId { get; set; }
        public string CausationId { get; set; }
        public string TenantId { get; set; }
        public string Source { get; set; }
        public string Payload { get; set; }
        public string ContentType { get; set; }

        public static OutboxRecord From(EventEnvelope envelope, string shard)
        {
            return new OutboxRecord()
            {
                id = envelope.EventId,
                PartitionKey = shard,
                RecordedAtTicks = DateTime.UtcNow.Ticks,
                Sent = false,
                SchemaId = envelope.SchemaId,
                Channel = envelope.Channel,
                EventPartitionKey = envelope.PartitionKey,
                OccurredAt = envelope.OccurredAt,
                CorrelationId = envelope.CorrelationId,
                CausationId = envelope.CausationId,
                TenantId = envelope.TenantId,
                Source = envelope.Source,
                Payload = envelope.Payload,
                ContentType = envelope.ContentType,
            };
        }

        public EventEnvelope ToEnvelope()
        {
            return new EventEnvelope()
            {
                EventId = id,
                SchemaId = SchemaId,
                Channel = Channel,
                PartitionKey = EventPartitionKey,
                OccurredAt = OccurredAt,
                CorrelationId = CorrelationId,
                CausationId = CausationId,
                TenantId = TenantId,
                Source = Source,
                Payload = Payload,
                ContentType = ContentType,
            };
        }
    }

    /// <summary>
    /// What a consumer group has already processed.
    ///
    /// The event id is the document id and the consumer group is the partition, so recognising a
    /// redelivery is a single partitioned insert that either succeeds or reports a duplicate - not a
    /// read followed by a write, which two racing consumers would both pass.
    /// </summary>
    public sealed class InboxRecord : Entity, IDocument
    {
        public string SchemaId { get; set; }
        public DateTimeOffset ProcessedAt { get; set; }
    }
}
