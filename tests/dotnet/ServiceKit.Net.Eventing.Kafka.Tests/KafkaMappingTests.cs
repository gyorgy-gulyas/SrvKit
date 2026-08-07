using System.Text;
using Confluent.Kafka;
using ServiceKit.Net.Eventing.Kafka;

namespace ServiceKit.Net.Eventing.Kafka.Tests
{
    /// <summary>
    /// The parts of the adapter that are decisions rather than plumbing - and that therefore do not
    /// need a broker to check. These run everywhere; the conformance suite next door needs Kafka.
    /// </summary>
    [TestClass]
    public class KafkaMappingTests
    {
        [TestMethod]
        public void A_channel_becomes_a_topic_kafka_will_not_complain_about()
        {
            // Kafka warns about topics mixing '.' and '_' - they collide in metric names - so the
            // dots in a channel name become dashes.
            var options = new KafkaEventBrokerOptions();
            Assert.AreEqual("WebShop-Sales", options.TopicFor("WebShop.Sales"));
        }

        [TestMethod]
        public void A_prefix_keeps_environments_apart_on_a_shared_cluster()
        {
            var options = new KafkaEventBrokerOptions() { TopicPrefix = "staging." };
            Assert.AreEqual("staging.WebShop-Sales", options.TopicFor("WebShop.Sales"));
        }

        [TestMethod]
        public void An_explicit_mapping_wins()
        {
            // For the deployments where the topic already exists under somebody else's name.
            var options = new KafkaEventBrokerOptions() { TopicPrefix = "staging." };
            options.Topics["WebShop.Sales"] = "legacy_orders_v2";

            Assert.AreEqual("legacy_orders_v2", options.TopicFor("WebShop.Sales"));
        }

        [TestMethod]
        public void The_envelope_travels_as_headers_and_the_payload_stays_the_contract()
        {
            // The payload is exactly what the model declared. A consumer in another language, or a
            // tool inspecting the topic, reads the envelope without deserializing a schema it may
            // not have.
            var envelope = new EventEnvelope()
            {
                EventId = "e-1",
                SchemaId = "WebShop.Sales.Order.OrderPlaced.v1",
                Channel = "WebShop.Sales",
                PartitionKey = "O-1",
                OccurredAt = DateTimeOffset.Parse("2026-08-07T10:11:12.1234567+02:00"),
                CorrelationId = "corr-1",
                CausationId = "cause-1",
                TenantId = "tenant-1",
                Source = "WebShop.Sales",
                Payload = "{\"orderId\":\"O-1\"}",
                ContentType = "application/json",
            };

            var headers = KafkaEnvelopeHeaders.From(envelope);

            string Header(string name)
            {
                headers.TryGetLastBytes(name, out var bytes);
                return bytes == null ? null : Encoding.UTF8.GetString(bytes);
            }

            Assert.AreEqual("e-1", Header(KafkaEnvelopeHeaders.EventId));
            Assert.AreEqual("WebShop.Sales.Order.OrderPlaced.v1", Header(KafkaEnvelopeHeaders.SchemaId));
            Assert.AreEqual("corr-1", Header(KafkaEnvelopeHeaders.CorrelationId));
            Assert.AreEqual("tenant-1", Header(KafkaEnvelopeHeaders.TenantId));
        }

        [TestMethod]
        public void The_instant_survives_a_timezone_change()
        {
            // Round-trip format, offset included: a consumer elsewhere has to read the same instant,
            // not a plausible-looking different one.
            var occurred = DateTimeOffset.Parse("2026-08-07T10:11:12.1234567+02:00");
            var envelope = new EventEnvelope() { EventId = "e-2", PartitionKey = "O-2", OccurredAt = occurred, Payload = "{}" };

            var result = Consumed(KafkaEnvelopeHeaders.From(envelope), key: "O-2", value: "{}");
            var back = KafkaEnvelopeHeaders.ToEnvelope(result, "WebShop.Sales");

            Assert.AreEqual(occurred.UtcTicks, back.OccurredAt.UtcTicks);
        }

        [TestMethod]
        public void The_ordering_scope_comes_back_from_the_message_key()
        {
            // The partition key IS the Kafka key: everything the platform promises about order
            // rests on that, so it must survive the round trip.
            var envelope = new EventEnvelope() { EventId = "e-3", PartitionKey = "O-3", OccurredAt = DateTimeOffset.UtcNow, Payload = "{}" };

            var back = KafkaEnvelopeHeaders.ToEnvelope(Consumed(KafkaEnvelopeHeaders.From(envelope), "O-3", "{}"), "WebShop.Sales");
            Assert.AreEqual("O-3", back.PartitionKey);
        }

        [TestMethod]
        public void A_message_without_our_headers_still_arrives_readable()
        {
            // Somebody else's producer on the same topic, or an older one. Falling over would be
            // worse than delivering what is there and letting dispatch find no handler for it.
            var back = KafkaEnvelopeHeaders.ToEnvelope(Consumed(new Headers(), "K-1", "{\"a\":1}"), "WebShop.Sales");

            Assert.AreEqual("K-1", back.PartitionKey);
            Assert.AreEqual("{\"a\":1}", back.Payload);
            Assert.AreEqual("application/json", back.ContentType);
            Assert.IsNull(back.SchemaId);
        }

        private static ConsumeResult<string, string> Consumed(Headers headers, string key, string value)
        {
            return new ConsumeResult<string, string>()
            {
                Topic = "WebShop-Sales",
                Partition = new Partition(0),
                Offset = new Offset(1),
                Message = new Message<string, string>()
                {
                    Key = key,
                    Value = value,
                    Headers = headers,
                    Timestamp = new Timestamp(DateTime.UtcNow),
                },
            };
        }
    }
}
