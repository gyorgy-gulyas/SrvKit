using System.Text.Json;

namespace ServiceKit.Net.Eventing.Tests
{
    /// <summary>Stands in for what the emitter will generate from `audit record`.</summary>
    public sealed class OrderExported_v1 : IAuditFact
    {
        public string SchemaId => "WebShop.Sales.OrderExported.v1";
        public string Channel => "WebShop.Sales";

        public string orderId { get; set; }
        public string exportedBy { get; set; }
    }

    /// <summary>
    /// Same pipe, two facades. The tests are mostly about what the type system refuses.
    /// </summary>
    [TestClass]
    public class AuditFactTests
    {
        private static EventRecorder NewRecorder() => new EventRecorder(new JsonEventSerializer());

        [TestMethod]
        public void An_audit_fact_is_not_an_event()
        {
            // The whole reason the two interfaces are separate. If IAuditFact were an IDomainEvent,
            // evidence could be announced as something to react to - and nothing reacts to evidence.
            Assert.IsFalse(typeof(IDomainEvent).IsAssignableFrom(typeof(IAuditFact)));
            Assert.IsFalse(typeof(IAuditFact).IsAssignableFrom(typeof(IDomainEvent)));
            Assert.IsFalse(typeof(IDomainEvent).IsAssignableFrom(typeof(OrderExported_v1)));
        }

        [TestMethod]
        public void Nothing_can_subscribe_to_an_audit_fact()
        {
            // IEventHandler<T> is constrained to IDomainEvent, so IEventHandler<OrderExported_v1>
            // does not exist as a type - there is no handler to register and no dispatch to reach.
            var closed = typeof(IEventHandler<>).GetGenericArguments()[0].GetGenericParameterConstraints();
            CollectionAssert.Contains(closed, typeof(IDomainEvent));
            Assert.IsFalse(closed.Contains(typeof(IAuditFact)));
        }

        [TestMethod]
        public void The_event_facade_will_not_take_evidence()
        {
            // IEventRecorder.Record takes an IDomainEvent, so this is a compile error rather than a
            // review comment. Asserted on the signature so the guarantee is checked, not assumed.
            var record = typeof(IEventRecorder).GetMethod(nameof(IEventRecorder.Record));
            Assert.AreEqual(typeof(IDomainEvent), record.GetParameters()[0].ParameterType);

            var auditRecord = typeof(IAuditRecorder).GetMethod(nameof(IAuditRecorder.Record));
            Assert.AreEqual(typeof(IAuditFact), auditRecord.GetParameters()[0].ParameterType);
        }

        [TestMethod]
        public void Evidence_travels_the_same_pipe()
        {
            IAuditRecorder audit = NewRecorder();
            audit.Record(new OrderExported_v1() { orderId = "O-1", exportedBy = "gy" }, "O-1");

            var envelope = ((IEventRecorder)audit).Drain().Single();
            Assert.AreEqual("WebShop.Sales.OrderExported.v1", envelope.SchemaId);
            Assert.AreEqual("O-1", envelope.PartitionKey);
        }

        [TestMethod]
        public void The_routing_constants_stay_off_the_wire_for_evidence_too()
        {
            IAuditRecorder audit = NewRecorder();
            audit.Record(new OrderExported_v1() { orderId = "O-2", exportedBy = "gy" }, "O-2");

            var payload = ((IEventRecorder)audit).Drain().Single().Payload;
            using var parsed = JsonDocument.Parse(payload);

            Assert.AreEqual("O-2", parsed.RootElement.GetProperty("orderId").GetString());
            Assert.AreEqual("gy", parsed.RootElement.GetProperty("exportedBy").GetString());
            Assert.IsFalse(parsed.RootElement.TryGetProperty("SchemaId", out _));
            Assert.IsFalse(parsed.RootElement.TryGetProperty("Channel", out _));
        }

        [TestMethod]
        public void Evidence_needs_an_ordering_scope_like_anything_else()
        {
            IAuditRecorder audit = NewRecorder();
            Assert.ThrowsException<ArgumentException>(() => audit.Record(new OrderExported_v1(), ""));
        }

        [TestMethod]
        public void Unsent_evidence_is_reported_like_an_unsent_event()
        {
            // The guard does not care which facade recorded it: evidence that never left is exactly
            // as lost as a fact that never left.
            var recorder = NewRecorder();
            ((IAuditRecorder)recorder).Record(new OrderExported_v1() { orderId = "O-3" }, "O-3");

            Assert.IsTrue(((IEventRecorder)recorder).HasPending);
        }
    }
}
