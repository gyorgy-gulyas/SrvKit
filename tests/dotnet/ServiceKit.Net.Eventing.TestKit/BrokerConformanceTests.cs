using System.Diagnostics;

namespace ServiceKit.Net.Eventing.TestKit
{
    /// <summary>
    /// What every broker adapter has to do, written once.
    ///
    /// This exists because "it works with the in-memory one" is not evidence about Kafka, and
    /// because each adapter re-deciding what the contract means is how two implementations of the
    /// same interface end up behaving differently in ways nobody notices until production. An
    /// adapter inherits this and is either conformant or is not.
    ///
    /// It deliberately tests only what the platform PROMISES. There is no test that facts of
    /// different partition keys arrive in any particular order, because no such promise was made
    /// and a test asserting it would be asserting an accident.
    /// </summary>
    public abstract class BrokerConformanceTests
    {
        /// <summary>A broker with nothing in it. Each test gets its own.</summary>
        protected abstract IEventBroker CreateBroker();

        /// <summary>
        /// A channel name no other test run is using.
        ///
        /// An in-memory broker forgets everything between tests; a real one does not, and two runs
        /// sharing a topic would read each other's leftovers.
        /// </summary>
        protected abstract string NewChannel();

        /// <summary>How long a delivery may take before the test gives up.</summary>
        protected virtual TimeSpan DeliveryTimeout => TimeSpan.FromSeconds(20);

        protected static EventEnvelope Envelope(string channel, string partitionKey, string eventId = null, string payload = "{}")
        {
            return new EventEnvelope()
            {
                EventId = eventId ?? Guid.NewGuid().ToString("D"),
                SchemaId = "WebShop.Sales.Order.OrderPlaced.v1",
                Channel = channel,
                PartitionKey = partitionKey,
                OccurredAt = DateTimeOffset.UtcNow,
                CorrelationId = "corr-1",
                CausationId = "cause-1",
                TenantId = "tenant-1",
                Source = "WebShop.Sales",
                Payload = payload,
                ContentType = "application/json",
            };
        }

        /// <summary>Waits for a condition rather than sleeping: a test that sleeps is flaky on somebody else's machine.</summary>
        protected async Task WaitUntil(Func<bool> condition, string what)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < DeliveryTimeout)
            {
                if (condition() == true)
                    return;
                await Task.Delay(25);
            }

            Assert.Fail($"Timed out after {DeliveryTimeout.TotalSeconds:0}s waiting for: {what}");
        }

        [TestMethod]
        public async Task A_published_fact_reaches_a_subscriber_of_its_channel()
        {
            var broker = CreateBroker();
            var channel = NewChannel();
            var received = new List<EventEnvelope>();

            await broker.Subscribe(channel, "group-a", (envelope, ct) =>
            {
                lock (received) { received.Add(envelope); }
                return Task.CompletedTask;
            });

            await broker.Publish(Envelope(channel, "O-1"));
            await WaitUntil(() => received.Count == 1, "the fact to arrive");
        }

        [TestMethod]
        public async Task Nothing_reaches_a_subscriber_of_another_channel()
        {
            var broker = CreateBroker();
            var listened = NewChannel();
            var other = NewChannel();
            var received = new List<EventEnvelope>();

            await broker.Subscribe(listened, "group-a", (envelope, ct) =>
            {
                lock (received) { received.Add(envelope); }
                return Task.CompletedTask;
            });

            await broker.Publish(Envelope(other, "O-1"));
            await broker.Publish(Envelope(listened, "O-2"));

            await WaitUntil(() => received.Count == 1, "the fact of the listened channel to arrive");
            Assert.AreEqual("O-2", received[0].PartitionKey, "only the listened channel's fact may arrive");
        }

        [TestMethod]
        public async Task The_whole_envelope_survives_the_round_trip()
        {
            // Every one of these is load-bearing somewhere: the id is how a redelivery is
            // recognised, the schema id is how dispatch finds a handler, the partition key is the
            // ordering scope, and the correlation id is how the delivery is found in a trace.
            var broker = CreateBroker();
            var channel = NewChannel();
            EventEnvelope received = null;

            await broker.Subscribe(channel, "group-a", (envelope, ct) =>
            {
                received = envelope;
                return Task.CompletedTask;
            });

            var sent = Envelope(channel, "O-7", payload: "{\"orderId\":\"O-7\"}");
            await broker.Publish(sent);
            await WaitUntil(() => received != null, "the fact to arrive");

            Assert.AreEqual(sent.EventId, received.EventId);
            Assert.AreEqual(sent.SchemaId, received.SchemaId);
            Assert.AreEqual(sent.Channel, received.Channel);
            Assert.AreEqual(sent.PartitionKey, received.PartitionKey);
            Assert.AreEqual(sent.CorrelationId, received.CorrelationId);
            Assert.AreEqual(sent.CausationId, received.CausationId);
            Assert.AreEqual(sent.TenantId, received.TenantId);
            Assert.AreEqual(sent.Source, received.Source);
            Assert.AreEqual(sent.Payload, received.Payload);
            Assert.AreEqual(sent.ContentType, received.ContentType);
            Assert.AreEqual(sent.OccurredAt.ToUnixTimeMilliseconds(), received.OccurredAt.ToUnixTimeMilliseconds());
        }

        [TestMethod]
        public async Task Facts_of_one_partition_key_keep_their_order()
        {
            // The ONE ordering promise the platform makes. Between different keys there is no
            // promise, and this test deliberately does not make one.
            var broker = CreateBroker();
            var channel = NewChannel();
            var received = new List<string>();

            await broker.Subscribe(channel, "group-a", (envelope, ct) =>
            {
                lock (received) { received.Add(envelope.EventId); }
                return Task.CompletedTask;
            });

            await broker.Publish(Envelope(channel, "O-9", eventId: "first"));
            await broker.Publish(Envelope(channel, "O-9", eventId: "second"));
            await broker.Publish(Envelope(channel, "O-9", eventId: "third"));

            await WaitUntil(() => received.Count == 3, "all three facts to arrive");
            CollectionAssert.AreEqual(new[] { "first", "second", "third" }, received.ToArray());
        }

        [TestMethod]
        public async Task Two_consumer_groups_each_get_their_own_copy()
        {
            // Two groups are two independent readers. One having seen a fact says nothing about
            // the other - which is what makes adding a new consumer a safe thing to do.
            var broker = CreateBroker();
            var channel = NewChannel();
            int a = 0, b = 0;

            await broker.Subscribe(channel, "group-a", (envelope, ct) => { Interlocked.Increment(ref a); return Task.CompletedTask; });
            await broker.Subscribe(channel, "group-b", (envelope, ct) => { Interlocked.Increment(ref b); return Task.CompletedTask; });

            await broker.Publish(Envelope(channel, "O-3"));
            await WaitUntil(() => a == 1 && b == 1, "both groups to receive the fact");
        }

        [TestMethod]
        public async Task A_handler_that_throws_gets_the_fact_again()
        {
            // Throwing is a nack. Without redelivery the at-least-once promise is not kept, and a
            // transient failure would silently lose a fact.
            var broker = CreateBroker();
            var channel = NewChannel();
            int attempts = 0;

            await broker.Subscribe(channel, "group-a", (envelope, ct) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new InvalidOperationException("not this time");
                return Task.CompletedTask;
            });

            await broker.Publish(Envelope(channel, "O-4"));
            await WaitUntil(() => attempts >= 2, "the fact to be delivered again after a failure");
        }

        [TestMethod]
        public async Task A_redelivery_says_which_attempt_it_is()
        {
            // The delivery pipeline decides when to give up, and it can only do that if the
            // transport counts. A broker that always reports attempt 1 turns a poison message into
            // an infinite loop.
            var broker = CreateBroker();
            var channel = NewChannel();
            var attempts = new List<int>();

            await broker.Subscribe(channel, "group-a", (envelope, ct) =>
            {
                lock (attempts) { attempts.Add(envelope.Attempt); }
                if (envelope.Attempt < 3)
                    throw new InvalidOperationException("not yet");
                return Task.CompletedTask;
            });

            await broker.Publish(Envelope(channel, "O-5"));
            await WaitUntil(() => attempts.Count >= 3, "three delivery attempts");

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, attempts.Take(3).ToArray());
        }

        [TestMethod]
        public async Task A_subscriber_added_before_the_publish_is_the_one_that_matters()
        {
            // Nothing arrives that was published before anyone was listening. This is not a
            // criticism of the broker - it is why the OUTBOX is the durable part, not the topic.
            var broker = CreateBroker();
            var channel = NewChannel();
            int received = 0;

            await broker.Subscribe(channel, "group-a", (envelope, ct) => { Interlocked.Increment(ref received); return Task.CompletedTask; });
            await broker.Publish(Envelope(channel, "O-6"));

            await WaitUntil(() => received == 1, "the fact to arrive");
            Assert.AreEqual(1, received);
        }
    }
}
