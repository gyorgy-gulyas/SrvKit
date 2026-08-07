using PolyPersist;
using PolyPersist.Net.Core;
using PolyPersist.Net.DocumentStore.Memory;
using PolyPersist.Net.Transactions;
using ServiceKit.Net.Eventing.PolyPersistStores;

namespace ServiceKit.Net.Eventing.PolyPersist.Tests
{
    /// <summary>Stands in for the domain row a repository saves alongside the fact.</summary>
    public class OrderRow : Entity, IDocument
    {
        public string status { get; set; }
    }

    public sealed class OrderPlaced_v1 : IDomainEvent
    {
        public string SchemaId => "WebShop.Sales.Order.OrderPlaced.v1";
        public string Channel => "WebShop.Sales";
        public string orderId { get; set; }
    }

    [TestClass]
    public class OutboxTests
    {
        private IDocumentStore _store;
        private IDocumentCollection<OutboxRecord> _outboxCollection;
        private IDocumentCollection<OrderRow> _orders;

        [TestInitialize]
        public async Task Setup()
        {
            _store = new Memory_DocumentStore("");
            _outboxCollection = await _store.CreateCollection<OutboxRecord>("outbox");
            _orders = await _store.CreateCollection<OrderRow>("orders");
        }

        private PolyPersistOutboxStore NewOutbox(int shardCount = 1)
            => new PolyPersistOutboxStore(_outboxCollection, new PolyPersistOutboxOptions() { ShardCount = shardCount });

        private static EventEnvelope Envelope(string eventId, string partitionKey)
        {
            return new EventEnvelope()
            {
                EventId = eventId,
                SchemaId = "WebShop.Sales.Order.OrderPlaced.v1",
                Channel = "WebShop.Sales",
                PartitionKey = partitionKey,
                OccurredAt = DateTimeOffset.UtcNow,
                CorrelationId = "corr-1",
                Payload = "{\"orderId\":\"" + partitionKey + "\"}",
                ContentType = "application/json",
            };
        }

        [TestMethod]
        public async Task Nothing_is_written_before_the_commit()
        {
            // PolyPersist queues every operation and replays them at Commit, so an append that is
            // never committed leaves nothing behind - and no reader ever saw a half-finished change.
            var outbox = NewOutbox();
            var transaction = new Transaction();

            await outbox.Append(new[] { Envelope("e-1", "O-1") }, transaction.AsOutboxTransaction());

            Assert.AreEqual(0, (await outbox.ReadUnsent(10)).Count, "the outbox must be empty before the commit");
        }

        [TestMethod]
        public async Task A_rolled_back_save_leaves_no_fact_behind()
        {
            // The half the design cares about most: the state was not saved, so the world must not
            // be told anything happened.
            var outbox = NewOutbox();
            var transaction = new Transaction();

            await transaction.Insert(_orders, new OrderRow() { id = "O-2", PartitionKey = "O-2", status = "Placed" });
            await outbox.Append(new[] { Envelope("e-2", "O-2") }, transaction.AsOutboxTransaction());
            await transaction.Rollback();

            Assert.IsNull(await _orders.Find("O-2", "O-2"));
            Assert.AreEqual(0, (await outbox.ReadUnsent(10)).Count);
        }

        [TestMethod]
        public async Task The_state_and_the_fact_land_in_the_same_commit()
        {
            var outbox = NewOutbox();
            var transaction = new Transaction();

            await transaction.Insert(_orders, new OrderRow() { id = "O-3", PartitionKey = "O-3", status = "Placed" });
            await outbox.Append(new[] { Envelope("e-3", "O-3") }, transaction.AsOutboxTransaction());
            await transaction.Commit();

            Assert.IsNotNull(await _orders.Find("O-3", "O-3"));

            var unsent = await outbox.ReadUnsent(10);
            Assert.AreEqual(1, unsent.Count);
            Assert.AreEqual("e-3", unsent[0].EventId);
            Assert.AreEqual("O-3", unsent[0].PartitionKey, "the event keeps its own ordering key");
            Assert.AreEqual("corr-1", unsent[0].CorrelationId);
        }

        [TestMethod]
        public async Task A_fact_recorded_on_its_own_needs_no_transaction()
        {
            // No transaction means there is no domain state to be atomic WITH - a context-level
            // fact recorded by itself. Writing directly is correct there, not a shortcut.
            var outbox = NewOutbox();
            await outbox.Append(new[] { Envelope("e-4", "X-1") }, transaction: null);

            Assert.AreEqual(1, (await outbox.ReadUnsent(10)).Count);
        }

        [TestMethod]
        public async Task The_relay_reads_in_recording_order()
        {
            var outbox = NewOutbox();
            await outbox.Append(new[] { Envelope("e-a", "O-9"), Envelope("e-b", "O-9"), Envelope("e-c", "O-9") }, null);

            var unsent = await outbox.ReadUnsent(10);
            CollectionAssert.AreEqual(new[] { "e-a", "e-b", "e-c" }, unsent.Select(e => e.EventId).ToArray());
        }

        [TestMethod]
        public async Task A_sent_fact_is_not_read_again()
        {
            var outbox = NewOutbox();
            await outbox.Append(new[] { Envelope("e-5", "O-5"), Envelope("e-6", "O-6") }, null);

            await outbox.MarkSent(new[] { "e-5" });

            var unsent = await outbox.ReadUnsent(10);
            Assert.AreEqual(1, unsent.Count);
            Assert.AreEqual("e-6", unsent[0].EventId);
        }

        [TestMethod]
        public async Task A_failed_attempt_stays_unsent_and_says_why()
        {
            // A stuck fact has to be something an operator can see, not infer.
            var outbox = NewOutbox();
            await outbox.Append(new[] { Envelope("e-7", "O-7") }, null);

            await outbox.MarkAttemptFailed("e-7", "the broker is unreachable");

            Assert.AreEqual(1, (await outbox.ReadUnsent(10)).Count);

            var stored = await _outboxCollection.Find("outbox", "e-7");
            Assert.AreEqual(1, stored.FailedAttempts);
            StringAssert.Contains(stored.LastFailure, "unreachable");
        }

        [TestMethod]
        public async Task A_huge_failure_reason_is_truncated()
        {
            // The outbox is not a log; a broker client's stack trace does not belong in a row that
            // is read on every relay pass.
            var outbox = NewOutbox();
            await outbox.Append(new[] { Envelope("e-8", "O-8") }, null);

            await outbox.MarkAttemptFailed("e-8", new string('x', 5000));

            var stored = await _outboxCollection.Find("outbox", "e-8");
            Assert.AreEqual(512, stored.LastFailure.Length);
        }

        [TestMethod]
        public async Task One_aggregate_s_facts_never_split_across_shards()
        {
            // If they did, the one ordering promise the platform makes would break the moment a
            // second relay worker started.
            var outbox = NewOutbox(shardCount: 8);

            var first = outbox.ShardFor("O-42");
            for (int i = 0; i < 20; i++)
                Assert.AreEqual(first, outbox.ShardFor("O-42"));

            await outbox.Append(new[] { Envelope("e-x", "O-42"), Envelope("e-y", "O-42") }, null);

            var stored = _outboxCollection.QueryCrossPartition().ToList();
            Assert.AreEqual(1, stored.Select(r => r.PartitionKey).Distinct().Count());
        }

        [TestMethod]
        public void The_shard_of_a_key_survives_a_restart()
        {
            // string.GetHashCode() is randomized per process by design, so using it would scatter an
            // aggregate's facts across shards after every restart - and take its ordering with them.
            var outbox = NewOutbox(shardCount: 16);

            // These values are pinned deliberately: changing the hash silently re-shards every key
            // and reorders facts that were promised to stay in order.
            Assert.AreEqual("outbox-8", outbox.ShardFor("O-1"));
            Assert.AreEqual("outbox-1", outbox.ShardFor("O-2"));
            Assert.AreEqual("outbox-2", outbox.ShardFor("customer-77"));
        }

        [TestMethod]
        public async Task Sharded_reads_still_find_everything()
        {
            var outbox = NewOutbox(shardCount: 4);
            await outbox.Append(Enumerable.Range(0, 12).Select(i => Envelope($"e-{i}", $"O-{i}")).ToArray(), null);

            var unsent = await outbox.ReadUnsent(100);
            Assert.AreEqual(12, unsent.Count);

            await outbox.MarkSent(unsent.Select(e => e.EventId).ToArray());
            Assert.AreEqual(0, (await outbox.ReadUnsent(100)).Count, "MarkSent has to find rows in every shard");
        }
    }
}
