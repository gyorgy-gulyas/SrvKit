using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceKit.Net.Eventing.InMemory;

namespace ServiceKit.Net.Eventing.Tests
{
    /// <summary>
    /// The whole path, with no infrastructure: record, outbox, relay, broker, dedup, dispatch,
    /// dead letter. Every one of these runs in the test process and takes the same route production
    /// takes - which is the only reason a contract test is worth anything.
    /// </summary>
    [TestClass]
    public class DeliveryTests
    {
        private ServiceProvider _provider;
        private InMemoryOutboxStore _outbox;
        private InMemoryDeadLetterSink _deadLetters;
        private OutboxRelay _relay;
        private EventSubscriberHost _subscriber;
        private IEventRecorder _recorder;

        [TestInitialize]
        public void Setup()
        {
            OrderPlacedHandler.Reset();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddServiceKitEventing(options =>
            {
                options.ConsumerGroup = "sales-tests";
                options.MaxDeliveryAttempts = 3;
                options.RunRelay = false;   // the tests drive the relay explicitly; a sleeping test is a flaky test
            });
            services.UseEventing_InMemory();
            services.AddEventHandler<OrderPlacedHandler, OrderPlaced_v1>();

            _provider = services.BuildServiceProvider();
            _outbox = _provider.GetRequiredService<InMemoryOutboxStore>();
            _deadLetters = _provider.GetRequiredService<InMemoryDeadLetterSink>();
            _recorder = _provider.GetRequiredService<IServiceScopeFactory>().CreateScope().ServiceProvider.GetRequiredService<IEventRecorder>();

            _relay = new OutboxRelay(
                _outbox,
                _provider.GetRequiredService<IEventBroker>(),
                _provider.GetRequiredService<IOptions<EventingOptions>>(),
                NullLogger<OutboxRelay>.Instance);

            _subscriber = new EventSubscriberHost(
                _provider.GetRequiredService<EventSubscriptionRegistry>(),
                _provider.GetRequiredService<IEventBroker>(),
                _provider.GetRequiredService<IEventDispatcher>(),
                _deadLetters,
                _provider.GetRequiredService<IOptions<EventingOptions>>(),
                NullLogger<EventSubscriberHost>.Instance);
        }

        [TestCleanup]
        public void Cleanup() => _provider?.Dispose();

        private async Task RecordAndSave(IDomainEvent @event, string partitionKey)
        {
            _recorder.Record(@event, partitionKey);
            // This is what the generated repository does inside the save: drain into the outbox as
            // part of the unit of work.
            await _outbox.Append(_recorder.Drain(), transaction: null);
        }

        private async Task Subscribe()
        {
            var broker = _provider.GetRequiredService<IEventBroker>();
            foreach (var channel in _provider.GetRequiredService<EventSubscriptionRegistry>().Channels)
                await broker.Subscribe(channel, "sales-tests", _subscriber.Deliver);
        }

        [TestMethod]
        public async Task Nothing_leaves_before_the_relay_runs()
        {
            await Subscribe();
            await RecordAndSave(new OrderPlaced_v1() { orderId = "O-1", totalPrice = 10 }, "O-1");

            // The fact is written down and it has not moved. If recording could publish, this is the
            // assertion that would be impossible to make.
            Assert.AreEqual(1, _outbox.Unsent.Count);
            Assert.AreEqual(0, OrderPlacedHandler.Seen.Count);
        }

        [TestMethod]
        public async Task The_relay_carries_the_fact_to_the_handler()
        {
            await Subscribe();
            await RecordAndSave(new OrderPlaced_v1() { orderId = "O-2", totalPrice = 199 }, "O-2");

            var published = await _relay.RunOnce();

            Assert.AreEqual(1, published);
            Assert.AreEqual(0, _outbox.Unsent.Count);
            Assert.AreEqual(1, OrderPlacedHandler.Seen.Count);
            Assert.AreEqual("O-2", OrderPlacedHandler.Seen[0].Event.orderId);
            Assert.AreEqual(199, OrderPlacedHandler.Seen[0].Event.totalPrice);
            Assert.AreEqual("O-2", OrderPlacedHandler.Seen[0].Context.PartitionKey);
        }

        [TestMethod]
        public async Task A_fact_nobody_listens_to_is_not_an_error()
        {
            // A channel carries every fact of its context; a subscriber cares about a few.
            await Subscribe();
            await RecordAndSave(new NobodyListens(), "X-1");

            Assert.AreEqual(1, await _relay.RunOnce());
            Assert.AreEqual(0, _deadLetters.Entries.Count);
        }

        [TestMethod]
        public async Task A_redelivered_fact_is_dropped()
        {
            // At-least-once is only a workable promise because the repeat is recognised.
            await Subscribe();
            await RecordAndSave(new OrderPlaced_v1() { orderId = "O-3" }, "O-3");
            await _relay.RunOnce();

            Assert.AreEqual(1, OrderPlacedHandler.Seen.Count);

            var envelope = _outbox.All.Single();
            await _subscriber.Deliver(envelope, CancellationToken.None);
            await _subscriber.Deliver(envelope, CancellationToken.None);

            Assert.AreEqual(1, OrderPlacedHandler.Seen.Count, "the same event id must not be processed twice");
        }

        [TestMethod]
        public async Task A_failed_attempt_comes_back()
        {
            // The reservation has to be released on failure, or a handler that threw once would
            // leave the event marked as seen and it would never be retried.
            await Subscribe();
            OrderPlacedHandler.FailTimes = 2;

            await RecordAndSave(new OrderPlaced_v1() { orderId = "O-4" }, "O-4");
            await _relay.RunOnce();

            Assert.AreEqual(1, OrderPlacedHandler.Seen.Count, "the third attempt should have succeeded");
            Assert.AreEqual(3, OrderPlacedHandler.Seen[0].Context.Attempt);
            Assert.AreEqual(0, _deadLetters.Entries.Count);
        }

        [TestMethod]
        public async Task A_fact_that_never_succeeds_is_dead_lettered_visibly()
        {
            // A failure that disappears is worse than one that stops the line.
            await Subscribe();
            OrderPlacedHandler.FailTimes = 99;

            await RecordAndSave(new OrderPlaced_v1() { orderId = "O-5" }, "O-5");
            await _relay.RunOnce();

            Assert.AreEqual(0, OrderPlacedHandler.Seen.Count);
            Assert.AreEqual(1, _deadLetters.Entries.Count);
            Assert.AreEqual("O-5", System.Text.Json.JsonDocument.Parse(_deadLetters.Entries[0].Envelope.Payload).RootElement.GetProperty("orderId").GetString());
            StringAssert.Contains(_deadLetters.Entries[0].Reason, "refused this attempt");
        }

        [TestMethod]
        public async Task A_broker_that_refuses_leaves_the_fact_in_the_outbox()
        {
            // This is the case the outbox exists for: the state is committed, the send fails, and
            // the intent survives because it was written down rather than held in memory.
            var refusing = new RefusingBroker();
            var relay = new OutboxRelay(_outbox, refusing, _provider.GetRequiredService<IOptions<EventingOptions>>(), NullLogger<OutboxRelay>.Instance);

            await RecordAndSave(new OrderPlaced_v1() { orderId = "O-6" }, "O-6");

            Assert.AreEqual(0, await relay.RunOnce());
            Assert.AreEqual(1, _outbox.Unsent.Count, "an unsent fact must stay unsent");
        }

        [TestMethod]
        public async Task Order_within_a_partition_key_survives_a_stuck_fact()
        {
            // Stepping over a stuck envelope would deliver the second fact of an order before the
            // first - and order within a key is the one ordering promise the platform makes.
            var refusing = new RefusingBroker() { FailFrom = 1 };
            var relay = new OutboxRelay(_outbox, refusing, _provider.GetRequiredService<IOptions<EventingOptions>>(), NullLogger<OutboxRelay>.Instance);

            await RecordAndSave(new OrderPlaced_v1() { orderId = "O-8" }, "O-8");
            await RecordAndSave(new OrderCancelled() { orderId = "O-8" }, "O-8");

            await relay.RunOnce();

            Assert.AreEqual(1, refusing.Published.Count, "the pass must stop at the first refusal");
            Assert.AreEqual(1, _outbox.Unsent.Count);
        }

        private sealed class RefusingBroker : IEventBroker
        {
            public List<EventEnvelope> Published { get; } = new();

            /// <summary>How many publishes succeed before the refusals start.</summary>
            public int FailFrom { get; set; } = 0;

            public Task Publish(EventEnvelope envelope, CancellationToken cancellationToken = default)
            {
                if (Published.Count >= FailFrom)
                    throw new IOException("the broker is unreachable");

                Published.Add(envelope);
                return Task.CompletedTask;
            }

            public Task Subscribe(string channel, string consumerGroup, Func<EventEnvelope, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
        }
    }
}
