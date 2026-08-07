using PolyPersist;
using PolyPersist.Net.DocumentStore.Memory;
using PolyPersist.Net.Transactions;
using ServiceKit.Net.Eventing.PolyPersistStores;

namespace ServiceKit.Net.Eventing.PolyPersist.Tests
{
    /// <summary>
    /// The point of the wrapper: what a developer must not forget is attached to the object they
    /// cannot avoid using. These tests are mostly about what CANNOT go wrong.
    /// </summary>
    [TestClass]
    public class OutboxTransactionTests
    {
        private IDocumentCollection<OrderRow> _orders;
        private PolyPersistOutboxStore _outbox;
        private EventRecorder _recorder;

        private sealed class OrderRoot : OrderRow, IEventRecordingRoot
        {
            private readonly List<RecordedEvent> _recorded = new();

            public void place() => _recorded.Add(new RecordedEvent(new OrderPlaced_v1() { orderId = id }, id));

            public IReadOnlyList<RecordedEvent> DrainRecordedEvents()
            {
                var drained = _recorded.ToArray();
                _recorded.Clear();
                return drained;
            }
        }

        [TestInitialize]
        public async Task Setup()
        {
            IDocumentStore store = new Memory_DocumentStore("");
            _orders = await store.CreateCollection<OrderRow>("orders");
            _outbox = new PolyPersistOutboxStore(await store.CreateCollection<OutboxRecord>("outbox"));
            _recorder = new EventRecorder(new JsonEventSerializer());
        }

        private OutboxTransaction NewTransaction() => new Transaction().WithOutbox(_outbox, _recorder);

        [TestMethod]
        public async Task Saving_a_root_carries_its_facts_without_a_second_call()
        {
            var root = new OrderRoot() { id = "O-1", PartitionKey = "O-1", status = "Placed" };
            root.place();

            var transaction = NewTransaction();
            await transaction.Insert(_orders, root);   // the ONLY call the developer makes
            await transaction.Commit();

            Assert.IsNotNull(await _orders.Find("O-1", "O-1"));

            var unsent = await _outbox.ReadUnsent(10);
            Assert.AreEqual(1, unsent.Count);
            Assert.AreEqual("O-1", unsent[0].PartitionKey);
        }

        [TestMethod]
        public async Task A_save_that_produced_no_fact_is_perfectly_normal()
        {
            // Requiring a fact would only teach people to invent one. An empty outbox is fine; a
            // recorded fact that never left is not.
            var row = new OrderRow() { id = "O-2", PartitionKey = "O-2", status = "Renamed" };

            var transaction = NewTransaction();
            await transaction.Insert(_orders, row);
            await transaction.Commit();

            Assert.IsNotNull(await _orders.Find("O-2", "O-2"));
            Assert.AreEqual(0, (await _outbox.ReadUnsent(10)).Count);
            Assert.AreEqual(0, transaction.QueuedFactCount);
        }

        [TestMethod]
        public async Task A_rolled_back_save_tells_nobody_anything()
        {
            var root = new OrderRoot() { id = "O-3", PartitionKey = "O-3", status = "Placed" };
            root.place();

            var transaction = NewTransaction();
            await transaction.Insert(_orders, root);
            await transaction.Rollback();

            Assert.IsNull(await _orders.Find("O-3", "O-3"));
            Assert.AreEqual(0, (await _outbox.ReadUnsent(10)).Count);
        }

        [TestMethod]
        public async Task The_root_is_emptied_so_a_second_save_does_not_repeat_the_fact()
        {
            var root = new OrderRoot() { id = "O-4", PartitionKey = "O-4", status = "Draft" };
            var setup = NewTransaction();
            await setup.Insert(_orders, root);
            await setup.Commit();

            root.place();

            var transaction = NewTransaction();
            transaction.AddOriginal(_orders, root);
            await transaction.Update(_orders, root);
            await transaction.Update(_orders, root);   // saved twice, ONE fact
            await transaction.Commit();

            Assert.AreEqual(1, (await _outbox.ReadUnsent(10)).Count);
            Assert.AreEqual(1, transaction.QueuedFactCount);
        }

        [TestMethod]
        public async Task An_update_carries_facts_too()
        {
            var root = new OrderRoot() { id = "O-5", PartitionKey = "O-5", status = "Draft" };
            var setup = NewTransaction();
            await setup.Insert(_orders, root);
            await setup.Commit();

            root.place();
            var transaction = NewTransaction();
            transaction.AddOriginal(_orders, root);
            await transaction.Update(_orders, root);
            await transaction.Commit();

            Assert.AreEqual(1, (await _outbox.ReadUnsent(10)).Count);
        }

        [TestMethod]
        public async Task A_deletion_is_something_that_happened_too()
        {
            var root = new OrderRoot() { id = "O-6", PartitionKey = "O-6", status = "Placed" };
            var setup = NewTransaction();
            await setup.Insert(_orders, root);
            await setup.Commit();
            await _outbox.MarkSent((await _outbox.ReadUnsent(10)).Select(e => e.EventId).ToArray());

            root.place();
            var transaction = NewTransaction();
            transaction.AddOriginal(_orders, root);
            await transaction.Delete(_orders, root);
            await transaction.Commit();

            Assert.AreEqual(1, (await _outbox.ReadUnsent(10)).Count);
        }

        [TestMethod]
        public async Task A_context_level_fact_can_be_queued_explicitly()
        {
            // Not everything has a root. A fact the context owns is recorded on the unit of work
            // directly, and travels with it just the same.
            var transaction = NewTransaction();
            await transaction.Record(new OrderPlaced_v1() { orderId = "day-2026-08-07" }, "day-2026-08-07");
            await transaction.Commit();

            Assert.AreEqual(1, (await _outbox.ReadUnsent(10)).Count);
        }

        [TestMethod]
        public async Task Saving_something_that_is_not_a_root_records_nothing()
        {
            var transaction = NewTransaction();
            await transaction.Insert(_orders, new OrderRow() { id = "O-7", PartitionKey = "O-7" });
            await transaction.Commit();

            Assert.AreEqual(0, transaction.QueuedFactCount);
        }
    }
}
