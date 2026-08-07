using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// Subscribes this process to every channel something in it listens to, and wraps each delivery
    /// with the two rules that are not the handler's business: how many attempts are allowed, and
    /// where an envelope goes when they run out.
    /// </summary>
    public sealed class EventSubscriberHost : BackgroundService
    {
        private readonly EventSubscriptionRegistry _registry;
        private readonly IEventBroker _broker;
        private readonly IEventDispatcher _dispatcher;
        private readonly IDeadLetterSink _deadLetters;
        private readonly EventingOptions _options;
        private readonly ILogger<EventSubscriberHost> _logger;

        public EventSubscriberHost(
            EventSubscriptionRegistry registry,
            IEventBroker broker,
            IEventDispatcher dispatcher,
            IDeadLetterSink deadLetters,
            IOptions<EventingOptions> options,
            ILogger<EventSubscriberHost> logger)
        {
            _registry = registry;
            _broker = broker;
            _dispatcher = dispatcher;
            _deadLetters = deadLetters;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            foreach (var channel in _registry.Channels)
            {
                _logger.LogInformation("Subscribing to channel {Channel} as {ConsumerGroup}.", channel, _options.ConsumerGroup);
                await _broker.Subscribe(channel, _options.ConsumerGroup, Deliver, stoppingToken);
            }
        }

        /// <summary>
        /// One delivery. Public so a test can push an envelope through the same path the broker
        /// uses, rather than through a private copy of the rules.
        /// </summary>
        public async Task Deliver(EventEnvelope envelope, CancellationToken cancellationToken)
        {
            // Attempt counting belongs to the transport, because only it knows a redelivery IS a
            // redelivery. An adapter for a broker that does not track it (Kafka does not) has to
            // keep the count itself - and must, or a poison message loops forever.
            if (envelope.Attempt > _options.MaxDeliveryAttempts)
            {
                await DeadLetter(envelope, $"Giving up after {envelope.Attempt - 1} failed attempts.", cancellationToken);
                return;
            }

            try
            {
                await _dispatcher.Dispatch(envelope, cancellationToken);
            }
            catch (Exception failure) when (envelope.Attempt >= _options.MaxDeliveryAttempts)
            {
                // The last attempt does not throw on: it dead-letters and acks, so the broker stops
                // handing it back. The failure has not disappeared - it is in the sink, with a
                // reason, and it can be replayed.
                await DeadLetter(envelope, failure.ToString(), cancellationToken);
            }
        }

        private async Task DeadLetter(EventEnvelope envelope, string reason, CancellationToken cancellationToken)
        {
            await _deadLetters.Send(envelope, reason, cancellationToken);
            EventingDiagnostics.DeadLettered.Add(1, new KeyValuePair<string, object>("schema_id", envelope.SchemaId));
            _logger.LogError("Event {SchemaId} {EventId} was dead-lettered: {Reason}", envelope.SchemaId, envelope.EventId, reason);
        }
    }
}
