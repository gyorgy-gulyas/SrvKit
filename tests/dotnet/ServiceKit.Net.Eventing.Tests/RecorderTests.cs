using System.Diagnostics;
using System.Text.Json;

namespace ServiceKit.Net.Eventing.Tests
{
    [TestClass]
    public class RecorderTests
    {
        private static EventRecorder NewRecorder(EventRecordingContext context = null)
            => new EventRecorder(new JsonEventSerializer(), context);

        [TestMethod]
        public void Recording_does_not_send_anything()
        {
            // The whole design rests on this: there is no way to publish from here. If there were,
            // the moment of sending would be independent of the transaction that saves the state.
            Assert.IsNull(typeof(IEventRecorder).GetMethod("Publish"));
            Assert.IsNull(typeof(IEventRecorder).GetMethod("PublishAsync"));
            Assert.IsNull(typeof(IEventRecorder).GetMethod("Send"));
        }

        [TestMethod]
        public void A_recorded_fact_waits_until_it_is_drained()
        {
            var recorder = NewRecorder();
            Assert.IsFalse(recorder.HasPending);

            recorder.Record(new OrderPlaced_v1() { orderId = "O-1", totalPrice = 100 }, "O-1");
            Assert.IsTrue(recorder.HasPending);

            var drained = recorder.Drain();
            Assert.AreEqual(1, drained.Count);

            // Drained means forgotten: a rolled-back save must not leave facts behind for the next
            // one to pick up.
            Assert.IsFalse(recorder.HasPending);
            Assert.AreEqual(0, recorder.Drain().Count);
        }

        [TestMethod]
        public void The_envelope_carries_what_delivery_and_tracing_need()
        {
            var recorder = NewRecorder(new EventRecordingContext()
            {
                CorrelationId = "corr-1",
                CausationId = "cause-1",
                TenantId = "tenant-1",
                Source = "WebShop.Sales",
            });

            recorder.Record(new OrderPlaced_v1() { orderId = "O-7", totalPrice = 250 }, "O-7");
            var envelope = recorder.Drain().Single();

            Assert.AreEqual("WebShop.Sales.Order.OrderPlaced.v1", envelope.SchemaId);
            Assert.AreEqual("WebShop.Sales", envelope.Channel);
            Assert.AreEqual("O-7", envelope.PartitionKey);
            Assert.AreEqual("corr-1", envelope.CorrelationId);
            Assert.AreEqual("cause-1", envelope.CausationId);
            Assert.AreEqual("tenant-1", envelope.TenantId);
            Assert.AreEqual("WebShop.Sales", envelope.Source);
            Assert.AreEqual("application/json", envelope.ContentType);
            Assert.IsTrue(Guid.TryParse(envelope.EventId, out _));
            Assert.AreNotEqual(default, envelope.OccurredAt);
        }

        [TestMethod]
        public void Every_recorded_fact_gets_its_own_identity()
        {
            var recorder = NewRecorder();
            recorder.Record(new OrderPlaced_v1() { orderId = "O-1" }, "O-1");
            recorder.Record(new OrderPlaced_v1() { orderId = "O-1" }, "O-1");

            var drained = recorder.Drain();
            Assert.AreNotEqual(drained[0].EventId, drained[1].EventId);
        }

        [TestMethod]
        public void The_correlation_id_is_the_trace_id_when_the_caller_brought_none()
        {
            // Two identifiers for one call means somebody has to pair them up by hand.
            using var listener = new ActivityListener()
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(listener);

            using var activity = new ActivitySource("test").StartActivity("recording");
            Assert.IsNotNull(activity, "the listener should have made the activity live");

            var recorder = NewRecorder();
            recorder.Record(new OrderPlaced_v1() { orderId = "O-1" }, "O-1");

            Assert.AreEqual(activity.TraceId.ToString(), recorder.Drain().Single().CorrelationId);
        }

        [TestMethod]
        public void A_fact_without_an_ordering_scope_is_refused()
        {
            // Handing it an arbitrary key would hide the modelling mistake instead of showing it.
            var recorder = NewRecorder();
            var failure = Assert.ThrowsException<ArgumentException>(
                () => recorder.Record(new OrderPlaced_v1() { orderId = "O-1" }, ""));

            StringAssert.Contains(failure.Message, "partition key");
        }

        [TestMethod]
        public void The_payload_keeps_the_model_s_own_names()
        {
            // Camel-casing here would mean the names on the wire are not the names in the model,
            // and a consumer reading a different casing is reading a different contract.
            var recorder = NewRecorder();
            recorder.Record(new OrderPlaced_v1() { orderId = "O-9", totalPrice = 42 }, "O-9");

            var payload = recorder.Drain().Single().Payload;
            using var parsed = JsonDocument.Parse(payload);

            Assert.IsTrue(parsed.RootElement.TryGetProperty("orderId", out var orderId));
            Assert.AreEqual("O-9", orderId.GetString());
            Assert.IsTrue(parsed.RootElement.TryGetProperty("totalPrice", out _));

            // The routing constants are not business data and have no place in the contract.
            Assert.IsFalse(parsed.RootElement.TryGetProperty("SchemaId", out _));
            Assert.IsFalse(parsed.RootElement.TryGetProperty("Channel", out _));
        }

        [TestMethod]
        public void Business_data_that_shares_a_name_with_a_routing_constant_survives()
        {
            // The routing constants are matched through the interface map, not by name - otherwise
            // a model that legitimately has a 'channel' field would silently lose it on the wire.
            var recorder = NewRecorder();
            recorder.Record(new NotifiedCustomer() { orderId = "O-11", channel = "email" }, "O-11");

            var envelope = recorder.Drain().Single();
            using var parsed = JsonDocument.Parse(envelope.Payload);

            Assert.AreEqual("email", parsed.RootElement.GetProperty("channel").GetString());
            Assert.IsFalse(parsed.RootElement.TryGetProperty("SchemaId", out _));
            Assert.AreEqual("WebShop.Sales", envelope.Channel, "routing still works through the interface");
        }

        [TestMethod]
        public void An_unversioned_fact_is_recorded_the_same_way()
        {
            // No version is a modelling statement about who the compatibility promise is made to,
            // not a special case for the pipeline.
            var recorder = NewRecorder();
            recorder.Record(new OrderCancelled() { orderId = "O-3", reason = "out of stock" }, "O-3");

            var envelope = recorder.Drain().Single();
            Assert.AreEqual("WebShop.Sales.Order.OrderCancelled", envelope.SchemaId);
        }
    }
}
