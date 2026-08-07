using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ServiceKit.Net.Eventing.InMemory;

namespace ServiceKit.Net.Eventing.Tests
{
    /// <summary>
    /// A handler may record facts of its own - that is how a chain of consequences forms, and it is
    /// what makes a published contract a reaction to an internal fact rather than a copy of it.
    /// Somebody has to take those facts, and nobody else is going to.
    /// </summary>
    [TestClass]
    public class HandlerRecordingTests
    {
        /// <summary>Reacts to a fact by recording another one - the shape of a translation.</summary>
        [AutoRegisterEventHandler]
        public sealed class ReactingHandler : IEventHandler<OrderPlaced_v1>
        {
            private readonly IEventRecorder _recorder;

            public ReactingHandler(IEventRecorder recorder) => _recorder = recorder;

            public Task Handle(EventContext context, OrderPlaced_v1 @event, CancellationToken cancellationToken = default)
            {
                _recorder.Record(new OrderCancelled() { orderId = @event.orderId, reason = "reacted" }, context.PartitionKey);
                return Task.CompletedTask;
            }
        }

        private ServiceProvider _provider;
        private InMemoryOutboxStore _outbox;
        private EventSubscriberHost _subscriber;

        [TestInitialize]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddServiceKitEventing(options => { options.ConsumerGroup = "sales-tests"; options.RunRelay = false; });
            services.UseEventing_InMemory();
            services.AddEventHandler<ReactingHandler, OrderPlaced_v1>();

            _provider = services.BuildServiceProvider();
            _outbox = _provider.GetRequiredService<InMemoryOutboxStore>();
            _subscriber = new EventSubscriberHost(
                _provider.GetRequiredService<EventSubscriptionRegistry>(),
                _provider.GetRequiredService<IEventBroker>(),
                _provider.GetRequiredService<IEventDispatcher>(),
                _provider.GetRequiredService<InMemoryDeadLetterSink>(),
                _provider.GetRequiredService<IOptions<EventingOptions>>(),
                NullLogger<EventSubscriberHost>.Instance);
        }

        [TestCleanup]
        public void Cleanup() => _provider?.Dispose();

        private static EventEnvelope Incoming(string orderId)
        {
            return new EventEnvelope()
            {
                EventId = Guid.NewGuid().ToString("D"),
                SchemaId = "WebShop.Sales.Order.OrderPlaced.v1",
                Channel = "WebShop.Sales",
                PartitionKey = orderId,
                OccurredAt = DateTimeOffset.UtcNow,
                CorrelationId = "corr-7",
                Payload = "{\"orderId\":\"" + orderId + "\"}",
                ContentType = "application/json",
                Attempt = 1,
            };
        }

        [TestMethod]
        public async Task A_fact_recorded_by_a_handler_reaches_the_outbox()
        {
            var incoming = Incoming("O-1");
            await _subscriber.Deliver(incoming, CancellationToken.None);

            var unsent = await _outbox.ReadUnsent(10);
            Assert.AreEqual(1, unsent.Count);
            Assert.AreEqual("WebShop.Sales.Order.OrderCancelled", unsent[0].SchemaId);
        }

        [TestMethod]
        public async Task The_new_fact_says_what_caused_it()
        {
            // This is what turns a pile of events into a chain that can be walked backwards - and
            // the reason the dispatcher sets the causation id before calling the handler.
            var incoming = Incoming("O-2");
            await _subscriber.Deliver(incoming, CancellationToken.None);

            var recorded = (await _outbox.ReadUnsent(10)).Single();
            Assert.AreEqual(incoming.EventId, recorded.CausationId);
            Assert.AreEqual("corr-7", recorded.CorrelationId, "the whole chain stays one call in a trace");
        }

        [TestMethod]
        public async Task The_reaction_keeps_the_ordering_scope_of_what_it_reacted_to()
        {
            await _subscriber.Deliver(Incoming("O-3"), CancellationToken.None);

            var recorded = (await _outbox.ReadUnsent(10)).Single();
            Assert.AreEqual("O-3", recorded.PartitionKey);
        }

        [TestMethod]
        public async Task A_handler_that_records_nothing_writes_nothing()
        {
            var incoming = Incoming("O-4");
            incoming.SchemaId = "nothing.listens.to.this";

            await _subscriber.Deliver(incoming, CancellationToken.None);
            Assert.AreEqual(0, (await _outbox.ReadUnsent(10)).Count);
        }
    }
}
