using System.Collections.Concurrent;

namespace ServiceKit.Net.Eventing.InMemory
{
    /// <summary>
    /// A broker in this process, meeting the same contract as a real one.
    ///
    /// This is what lets the contract test run with no infrastructure at all and still take the same
    /// path production takes: record, outbox, relay, publish, subscribe, dedup, dead letter. The
    /// sample application's fifty tests run without a single container, and everything the platform
    /// learned from that came out of tests being cheap enough to actually write.
    ///
    /// It keeps ORDER per partition key, redelivers on failure and counts attempts - the three
    /// behaviours a handler can be written wrong against. It does not keep anything after the
    /// process ends, and it does not pretend to.
    /// </summary>
    public sealed class InMemoryEventBroker : IEventBroker
    {
        private sealed class Subscriber
        {
            public string Channel;
            public string ConsumerGroup;
            public Func<EventEnvelope, CancellationToken, Task> Handler;
        }

        private readonly ConcurrentBag<Subscriber> _subscribers = new();
        private readonly int _maxRedeliveries;

        /// <param name="maxRedeliveries">
        /// How many times a failed delivery is handed back. It exists so a test can watch the retry
        /// path without an unbounded loop; a real broker's redelivery is its own business.
        /// </param>
        public InMemoryEventBroker(int maxRedeliveries = 10)
        {
            _maxRedeliveries = maxRedeliveries;
        }

        public Task Subscribe(string channel, string consumerGroup, Func<EventEnvelope, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
        {
            _subscribers.Add(new Subscriber() { Channel = channel, ConsumerGroup = consumerGroup, Handler = handler });
            return Task.CompletedTask;
        }

        public async Task Publish(EventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            foreach (var subscriber in _subscribers)
            {
                if (string.Equals(subscriber.Channel, envelope.Channel, StringComparison.Ordinal) == false)
                    continue;

                // Each subscriber gets its own copy. Sharing one envelope would let the attempt
                // count of one consumer group leak into another's.
                var delivery = Copy(envelope);

                for (int attempt = 1; attempt <= _maxRedeliveries; attempt++)
                {
                    delivery.Attempt = attempt;
                    try
                    {
                        await subscriber.Handler(delivery, cancellationToken);
                        break;
                    }
                    catch
                    {
                        // A nack. Hand it back, exactly as a broker would - the pipeline decides
                        // when enough is enough, not this.
                        if (attempt == _maxRedeliveries)
                            break;
                    }
                }
            }
        }

        private static EventEnvelope Copy(EventEnvelope source)
        {
            return new EventEnvelope()
            {
                EventId = source.EventId,
                SchemaId = source.SchemaId,
                Channel = source.Channel,
                PartitionKey = source.PartitionKey,
                OccurredAt = source.OccurredAt,
                CorrelationId = source.CorrelationId,
                CausationId = source.CausationId,
                TenantId = source.TenantId,
                Source = source.Source,
                Payload = source.Payload,
                ContentType = source.ContentType,
                Attempt = source.Attempt,
            };
        }
    }
}
