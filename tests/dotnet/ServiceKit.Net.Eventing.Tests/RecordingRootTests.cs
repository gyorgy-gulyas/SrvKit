namespace ServiceKit.Net.Eventing.Tests
{
    /// <summary>
    /// Stands in for a generated aggregate root: it keeps its own facts and needs no container to
    /// exist, which is the whole reason the root records into itself rather than into a service.
    /// </summary>
    public sealed class OrderRoot : IEventRecordingRoot
    {
        private readonly List<RecordedEvent> _recorded = new();

        public string orderId { get; set; }

        public IReadOnlyList<RecordedEvent> DrainRecordedEvents()
        {
            var drained = _recorded.ToArray();
            _recorded.Clear();
            return drained;
        }

        // Only the facts this root's commands declare with 'emits' get an overload. There is no
        // Record for anything else, so emitting one is a compile error rather than a review comment.
        public void Record(OrderPlaced_v1 @event) => _recorded.Add(new RecordedEvent(@event, orderId));

        public void place(decimal total)
        {
            Record(new OrderPlaced_v1() { orderId = orderId, totalPrice = total });
        }
    }

    [TestClass]
    public class RecordingRootTests
    {
        [TestMethod]
        public void The_root_records_into_itself()
        {
            var root = new OrderRoot() { orderId = "O-1" };
            root.place(100);

            var drained = root.DrainRecordedEvents();
            Assert.AreEqual(1, drained.Count);
            Assert.AreEqual("O-1", drained[0].PartitionKey);
            Assert.IsInstanceOfType(drained[0].Event, typeof(OrderPlaced_v1));
        }

        [TestMethod]
        public void A_root_that_was_not_saved_leaves_nothing_behind()
        {
            var root = new OrderRoot() { orderId = "O-2" };
            root.place(50);

            Assert.AreEqual(1, root.DrainRecordedEvents().Count);
            Assert.AreEqual(0, root.DrainRecordedEvents().Count);
        }

        [TestMethod]
        public void The_repository_moves_the_root_s_facts_into_the_unit_of_work()
        {
            var root = new OrderRoot() { orderId = "O-3" };
            root.place(75);

            var recorder = new EventRecorder(new JsonEventSerializer(), new EventRecordingContext() { CorrelationId = "corr-9" });
            recorder.RecordAll(root);

            var envelope = recorder.Drain().Single();
            Assert.AreEqual("WebShop.Sales.Order.OrderPlaced.v1", envelope.SchemaId);
            Assert.AreEqual("O-3", envelope.PartitionKey);
            Assert.AreEqual("corr-9", envelope.CorrelationId);
            Assert.AreEqual(0, root.DrainRecordedEvents().Count, "draining the root must empty it");
        }
    }
}
