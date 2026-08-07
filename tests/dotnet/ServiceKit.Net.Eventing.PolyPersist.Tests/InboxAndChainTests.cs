using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PolyPersist;
using PolyPersist.Net.DocumentStore.Memory;
using PolyPersist.Net.Transactions;
using ServiceKit.Net.Eventing.InMemory;
using ServiceKit.Net.Eventing.PolyPersistStores;

namespace ServiceKit.Net.Eventing.PolyPersist.Tests
{
    [TestClass]
    public class InboxTests
    {
        private IDocumentCollection<InboxRecord> _collection;
        private PolyPersistInboxStore _inbox;

        [TestInitialize]
        public async Task Setup()
        {
            IDocumentStore store = new Memory_DocumentStore("");
            _collection = await store.CreateCollection<InboxRecord>("inbox");
            _inbox = new PolyPersistInboxStore(_collection);
        }

        [TestMethod]
        public async Task The_first_delivery_wins_and_the_repeat_is_dropped()
        {
            Assert.IsTrue(await _inbox.TryBegin("sales", "e-1"));
            Assert.IsFalse(await _inbox.TryBegin("sales", "e-1"));
        }

        [TestMethod]
        public async Task Two_consumer_groups_are_independent()
        {
            // One group having seen an event says nothing about another: they are two readers of
            // the same stream, not one.
            Assert.IsTrue(await _inbox.TryBegin("sales", "e-2"));
            Assert.IsTrue(await _inbox.TryBegin("billing", "e-2"));
        }

        [TestMethod]
        public async Task An_abandoned_delivery_can_be_retried()
        {
            // Without this a handler that crashed halfway would leave the event marked as processed
            // and it would never come back.
            Assert.IsTrue(await _inbox.TryBegin("sales", "e-3"));
            await _inbox.Abandon("sales", "e-3");
            Assert.IsTrue(await _inbox.TryBegin("sales", "e-3"));
        }

        [TestMethod]
        public async Task Abandoning_something_that_is_already_gone_is_harmless()
        {
            await _inbox.Abandon("sales", "never-seen");
        }
    }

    /// <summary>
    /// The whole chain on real storage: a root records, the repository saves the state and the fact
    /// in one commit, the relay carries it out, the handler gets it - and a rollback tells nobody
    /// anything.
    /// </summary>
    [TestClass]
    public class ChainTests
    {
        private sealed class OrderPlacedHandler : IEventHandler<OrderPlaced_v1>
        {
            public static readonly List<string> Seen = new();

            public Task Handle(EventContext context, OrderPlaced_v1 @event, CancellationToken cancellationToken = default)
            {
                lock (Seen) { Seen.Add(@event.orderId); }
                return Task.CompletedTask;
            }
        }

        private IDocumentCollection<OrderRow> _orders;
        private PolyPersistOutboxStore _outbox;
        private InMemoryEventBroker _broker;
        private OutboxRelay _relay;

        [TestInitialize]
        public async Task Setup()
        {
            lock (OrderPlacedHandler.Seen) { OrderPlacedHandler.Seen.Clear(); }

            IDocumentStore store = new Memory_DocumentStore("");
            _orders = await store.CreateCollection<OrderRow>("orders");
            _outbox = new PolyPersistOutboxStore(await store.CreateCollection<OutboxRecord>("outbox"));

            _broker = new InMemoryEventBroker();
            _relay = new OutboxRelay(_outbox, _broker, Options.Create(new EventingOptions()), NullLogger<OutboxRelay>.Instance);

            await _broker.Subscribe("WebShop.Sales", "sales-tests", async (envelope, ct) =>
            {
                var payload = (OrderPlaced_v1)new JsonEventSerializer().Deserialize(envelope.Payload, typeof(OrderPlaced_v1));
                await new OrderPlacedHandler().Handle(EventContext.From(envelope), payload, ct);
            });
        }

        /// <summary>What a generated repository will do inside its save.</summary>
        private async Task Save(OrderRow row, IEventRecordingRoot root, bool commit)
        {
            var transaction = new Transaction();
            await transaction.Insert(_orders, row);

            var recorder = new EventRecorder(new JsonEventSerializer(), new EventRecordingContext() { Source = "WebShop.Sales" });
            recorder.RecordAll(root);
            await _outbox.Append(recorder.Drain(), transaction.AsOutboxTransaction());

            if (commit == true)
                await transaction.Commit();
            else
                await transaction.Rollback();
        }

        private sealed class Root : IEventRecordingRoot
        {
            private readonly List<RecordedEvent> _recorded = new();
            public string orderId { get; set; }

            public void place() => _recorded.Add(new RecordedEvent(new OrderPlaced_v1() { orderId = orderId }, orderId));

            public IReadOnlyList<RecordedEvent> DrainRecordedEvents()
            {
                var drained = _recorded.ToArray();
                _recorded.Clear();
                return drained;
            }
        }

        [TestMethod]
        public async Task A_committed_save_reaches_the_handler()
        {
            var root = new Root() { orderId = "O-1" };
            root.place();

            await Save(new OrderRow() { id = "O-1", PartitionKey = "O-1", status = "Placed" }, root, commit: true);
            await _relay.RunOnce();

            CollectionAssert.AreEqual(new[] { "O-1" }, OrderPlacedHandler.Seen.ToArray());
        }

        [TestMethod]
        public async Task A_rolled_back_save_tells_nobody_anything()
        {
            // The failure mode the outbox exists to prevent, from the other side: the state was not
            // saved, so the world must not react to it.
            var root = new Root() { orderId = "O-2" };
            root.place();

            await Save(new OrderRow() { id = "O-2", PartitionKey = "O-2", status = "Placed" }, root, commit: false);
            await _relay.RunOnce();

            Assert.AreEqual(0, OrderPlacedHandler.Seen.Count);
            Assert.IsNull(await _orders.Find("O-2", "O-2"));
        }

        [TestMethod]
        public async Task The_fact_survives_a_relay_that_never_ran()
        {
            // The point of writing the intent down: the process could have died here, and the fact
            // is still on disk waiting.
            var root = new Root() { orderId = "O-3" };
            root.place();

            await Save(new OrderRow() { id = "O-3", PartitionKey = "O-3", status = "Placed" }, root, commit: true);

            Assert.AreEqual(0, OrderPlacedHandler.Seen.Count);
            Assert.AreEqual(1, (await _outbox.ReadUnsent(10)).Count);

            // A later relay - a restart, another process - still delivers it.
            await _relay.RunOnce();
            CollectionAssert.AreEqual(new[] { "O-3" }, OrderPlacedHandler.Seen.ToArray());
        }
    }
}
