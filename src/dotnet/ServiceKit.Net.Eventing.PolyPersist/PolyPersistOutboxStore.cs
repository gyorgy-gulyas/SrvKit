using PolyPersist;
using PolyPersist.Net.Common;

namespace ServiceKit.Net.Eventing.PolyPersistStores
{
    /// <summary>
    /// Carries a PolyPersist transaction across the SrvKit boundary.
    ///
    /// SrvKit must not know what a transaction is made of, so <see cref="IOutboxTransaction"/> is an
    /// empty marker there and the unwrapping happens here, where PolyPersist is already a
    /// dependency.
    /// </summary>
    public sealed class PolyPersistOutboxTransaction : IOutboxTransaction
    {
        public PolyPersistOutboxTransaction(ITransaction transaction)
        {
            Transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        }

        public ITransaction Transaction { get; }
    }

    public sealed class PolyPersistOutboxOptions
    {
        /// <summary>
        /// How many shards the outbox is spread over.
        ///
        /// One is right until the relay cannot keep up. Raising it lets several relay workers drain
        /// in parallel without ever splitting one aggregate's facts across shards - but it is not a
        /// setting to change casually on a live system, because in-flight rows keep the shard they
        /// were written with.
        /// </summary>
        public int ShardCount { get; set; } = 1;

        public string ShardPrefix { get; set; } = "outbox";
    }

    /// <summary>
    /// The outbox on PolyPersist.
    ///
    /// ATOMICITY, said plainly, because this is the part it would be easy to overstate:
    ///
    /// PolyPersist's transaction writes NOTHING until Commit - every operation is queued and
    /// replayed there - so a failure before the commit costs nothing and no reader ever saw a
    /// half-finished change. Inside the commit, a store that can offer a native database
    /// transaction gets one and commits LAST, after every compensation-only store has succeeded.
    ///
    /// So: if the outbox collection lives in the same relational store as the domain write, the fact
    /// and the state land in ONE database transaction and the guarantee is real. If it lives in a
    /// store that can only compensate (a document store today), the window is the commit pass
    /// itself, and a failure there is compensated rather than rolled back. That is a real
    /// difference, it is backend-dependent, and the tests next to this class say which is which
    /// rather than leaving a reader to assume the stronger one.
    /// </summary>
    public sealed class PolyPersistOutboxStore : IOutboxStore
    {
        private readonly IDocumentCollection<OutboxRecord> _collection;
        private readonly PolyPersistOutboxOptions _options;

        public PolyPersistOutboxStore(IDocumentCollection<OutboxRecord> collection, PolyPersistOutboxOptions options = null)
        {
            _collection = collection ?? throw new ArgumentNullException(nameof(collection));
            _options = options ?? new PolyPersistOutboxOptions();
        }

        /// <summary>
        /// Which shard an ordering key belongs to. Deterministic and stable across processes and
        /// restarts - a hash that moved between runs would scatter one aggregate's facts and take
        /// its ordering with them.
        /// </summary>
        public string ShardFor(string eventPartitionKey)
        {
            if (_options.ShardCount <= 1)
                return _options.ShardPrefix;

            // FNV-1a rather than string.GetHashCode(): the latter is randomized per process by
            // design, so the same key would land in a different shard after every restart.
            uint hash = 2166136261;
            foreach (char c in eventPartitionKey ?? string.Empty)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return $"{_options.ShardPrefix}-{hash % (uint)_options.ShardCount}";
        }

        public async Task Append(IReadOnlyList<EventEnvelope> envelopes, IOutboxTransaction transaction, CancellationToken cancellationToken = default)
        {
            var polyPersistTransaction = (transaction as PolyPersistOutboxTransaction)?.Transaction;

            foreach (var envelope in envelopes)
            {
                var record = OutboxRecord.From(envelope, ShardFor(envelope.PartitionKey));

                if (polyPersistTransaction != null)
                {
                    // Queued, and replayed inside the same commit as the domain write. This is the
                    // whole reason the recorder has no Publish.
                    await polyPersistTransaction.Insert(_collection, record);
                }
                else
                {
                    // No transaction means the caller is not saving domain state alongside this -
                    // a context-level fact recorded on its own, say. There is nothing to be atomic
                    // WITH, so writing directly is correct rather than a shortcut.
                    await _collection.Insert(record);
                }

                EventingDiagnostics.Recorded.Add(1, new KeyValuePair<string, object>("schema_id", envelope.SchemaId));
            }
        }

        public Task<IReadOnlyList<EventEnvelope>> ReadUnsent(int maxCount, CancellationToken cancellationToken = default)
        {
            var found = new List<OutboxRecord>();

            // Shard by shard, each read partition-scoped. A cross-partition scan would work and
            // would get slower every day the outbox lives.
            for (int shard = 0; shard < Math.Max(1, _options.ShardCount) && found.Count < maxCount; shard++)
            {
                string partition = _options.ShardCount <= 1 ? _options.ShardPrefix : $"{_options.ShardPrefix}-{shard}";

                found.AddRange(_collection
                    .Query(partition)
                    .Where(r => r.Sent == false)
                    .OrderBy(r => r.RecordedAtTicks)
                    .ThenBy(r => r.id)
                    .Take(maxCount - found.Count)
                    .ToList());
            }

            IReadOnlyList<EventEnvelope> batch = found.Select(r => r.ToEnvelope()).ToArray();
            return Task.FromResult(batch);
        }

        public async Task MarkSent(IReadOnlyList<string> eventIds, CancellationToken cancellationToken = default)
        {
            foreach (var eventId in eventIds)
            {
                var record = await FindAcrossShards(eventId);
                if (record == null)
                    continue;

                record.Sent = true;
                await _collection.Update(record);
            }
        }

        public async Task MarkAttemptFailed(string eventId, string reason, CancellationToken cancellationToken = default)
        {
            var record = await FindAcrossShards(eventId);
            if (record == null)
                return;

            record.FailedAttempts = record.FailedAttempts + 1;
            // Truncated: a stack trace from a broker client can be enormous, and the outbox is not a
            // log. What is kept is enough to tell one failure from another.
            record.LastFailure = reason != null && reason.Length > 512 ? reason.Substring(0, 512) : reason;

            await _collection.Update(record);
        }

        private async Task<OutboxRecord> FindAcrossShards(string eventId)
        {
            // The relay knows the id but not the shard, and carrying the shard through the SrvKit
            // envelope would leak a storage detail into the delivery contract. With one shard this
            // is a single point read; with more it is at most ShardCount of them.
            for (int shard = 0; shard < Math.Max(1, _options.ShardCount); shard++)
            {
                string partition = _options.ShardCount <= 1 ? _options.ShardPrefix : $"{_options.ShardPrefix}-{shard}";

                var found = await _collection.Find(partition, eventId);
                if (found != null)
                    return found;
            }

            return null;
        }
    }

    /// <summary>
    /// The inbox on PolyPersist: one partitioned insert per delivery.
    /// </summary>
    public sealed class PolyPersistInboxStore : IInboxStore
    {
        private readonly IDocumentCollection<InboxRecord> _collection;

        public PolyPersistInboxStore(IDocumentCollection<InboxRecord> collection)
        {
            _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        }

        /// <summary>
        /// The document id.
        ///
        /// It is the group AND the event, not the event alone: on this platform `id` is a GLOBAL
        /// identity and `PartitionKey` is a routing key, so two consumer groups reserving the same
        /// event with the same id would collide - and the second group would silently conclude it
        /// had already processed something it never saw.
        /// </summary>
        private static string ReservationId(string consumerGroup, string eventId) => consumerGroup + ":" + eventId;

        public async Task<bool> TryBegin(string consumerGroup, string eventId, CancellationToken cancellationToken = default)
        {
            try
            {
                await _collection.Insert(new InboxRecord()
                {
                    id = ReservationId(consumerGroup, eventId),
                    PartitionKey = consumerGroup,
                    ProcessedAt = DateTimeOffset.UtcNow,
                });

                return true;
            }
            catch (DuplicateKeyException)
            {
                // Seen before. An insert that reports a duplicate IS the check - a read followed by
                // a write would let two racing consumers both decide they were first.
                return false;
            }
        }

        public async Task Abandon(string consumerGroup, string eventId, CancellationToken cancellationToken = default)
        {
            // Releases the reservation so a redelivery gets another chance. Without it a handler
            // that crashed halfway would leave the event marked as processed forever.
            try
            {
                await _collection.Delete(consumerGroup, ReservationId(consumerGroup, eventId));
            }
            catch (PolyPersistException)
            {
                // Already gone - two failures racing to release the same reservation. Nothing to do.
            }
        }
    }
}
