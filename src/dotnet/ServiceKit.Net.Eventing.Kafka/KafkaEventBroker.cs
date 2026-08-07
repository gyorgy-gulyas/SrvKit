using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ServiceKit.Net.Eventing.Kafka
{
    /// <summary>
    /// Kafka behind the same three-method contract as everything else.
    ///
    /// The mapping is the obvious one, and it is written down here rather than in the model:
    ///
    ///   logical channel  -> topic
    ///   partition key    -> message key, which is what gives Kafka the ordering we promise
    ///   consumer group   -> consumer group
    ///   envelope         -> headers; only the payload goes in the message value
    ///
    /// The envelope travels as headers on purpose. A consumer written in another language, or a
    /// tool inspecting the topic, can read who sent what and when without deserializing a payload
    /// whose schema it may not have - and the payload stays exactly the contract the model
    /// declared, with no platform fields mixed in.
    /// </summary>
    public sealed class KafkaEventBroker : IEventBroker, IDisposable
    {
        private readonly KafkaEventBrokerOptions _options;
        private readonly ILogger _logger;
        private readonly IProducer<string, string> _producer;
        // The pump THREADS, not the consumers. A consumer belongs to the thread that polls it and
        // is closed by that thread - see Dispose.
        private readonly ConcurrentBag<Thread> _pumps = new();
        private readonly CancellationTokenSource _stopping = new();

        public KafkaEventBroker(KafkaEventBrokerOptions options, ILogger<KafkaEventBroker> logger = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = (ILogger)logger ?? NullLogger.Instance;

            var producerConfig = new ProducerConfig()
            {
                BootstrapServers = _options.BootstrapServers,
                // Nothing is published that was not already committed to the outbox, so a
                // best-effort producer would trade a durability guarantee we already paid for
                // against latency we do not need.
                Acks = Acks.All,
                EnableIdempotence = true,
                MessageSendMaxRetries = _options.ProducerRetries,
            };
            _options.ConfigureProducer?.Invoke(producerConfig);

            _producer = new ProducerBuilder<string, string>(producerConfig).Build();
        }

        public string TopicFor(string channel) => _options.TopicFor(channel);

        public async Task Publish(EventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            var message = new Message<string, string>()
            {
                // The ordering scope IS the Kafka key. Everything the platform promises about order
                // rests on this one line.
                Key = envelope.PartitionKey,
                Value = envelope.Payload,
                Headers = KafkaEnvelopeHeaders.From(envelope),
            };

            await _producer.ProduceAsync(TopicFor(envelope.Channel), message, cancellationToken);
        }

        public Task Subscribe(string channel, string consumerGroup, Func<EventEnvelope, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
        {
            var topic = TopicFor(channel);

            var consumerConfig = new ConsumerConfig()
            {
                BootstrapServers = _options.BootstrapServers,
                GroupId = consumerGroup,
                // Manual commit, always. Committing before the handler ran would turn a crash into
                // a lost fact, which is the one thing this whole design exists to prevent.
                EnableAutoCommit = false,
                AutoOffsetReset = AutoOffsetReset.Earliest,
            };
            _options.ConfigureConsumer?.Invoke(consumerConfig);

            var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, cancellationToken);

            // A long-running loop, not a Task.Run of async work: Consume blocks, and putting a
            // blocking call on a thread-pool thread for the life of the process starves it.
            //
            // The consumer is BUILT ON and CLOSED BY this thread. librdkafka's consumer is a native
            // handle, and closing it from another thread while this one is inside
            // rd_kafka_consumer_poll is a use-after-free: it does not throw, it takes the process
            // down with an AccessViolationException that no catch block can see. Found by running
            // the conformance suite against a real broker - the in-memory one could never have
            // shown it.
            var pump = new Thread(() =>
            {
                var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
                try
                {
                    consumer.Subscribe(topic);
                    Pump(consumer, channel, handler, linked.Token);
                }
                finally
                {
                    try
                    {
                        // Close leaves the group tidily, so a rebalance is not delayed by a session
                        // timeout. It must happen here, on the only thread that ever polled.
                        consumer.Close();
                    }
                    catch (Exception failure)
                    {
                        _logger.LogWarning(failure, "Closing the consumer of {Topic} failed.", topic);
                    }

                    consumer.Dispose();
                    linked.Dispose();
                }
            })
            {
                IsBackground = true,
                Name = $"servicekit-eventing-{topic}-{consumerGroup}",
            };

            _pumps.Add(pump);
            pump.Start();

            return Task.CompletedTask;
        }

        private void Pump(IConsumer<string, string> consumer, string channel, Func<EventEnvelope, CancellationToken, Task> handler, CancellationToken cancellationToken)
        {
            while (cancellationToken.IsCancellationRequested == false)
            {
                ConsumeResult<string, string> result;
                try
                {
                    result = consumer.Consume(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception failure)
                {
                    _logger.LogError(failure, "Consuming from {Topic} failed; retrying.", consumer.Subscription.FirstOrDefault());
                    continue;
                }

                if (result?.Message == null)
                    continue;

                DeliverWithRetries(consumer, result, channel, handler, cancellationToken);
            }
        }

        private void DeliverWithRetries(IConsumer<string, string> consumer, ConsumeResult<string, string> result, string channel, Func<EventEnvelope, CancellationToken, Task> handler, CancellationToken cancellationToken)
        {
            var envelope = KafkaEnvelopeHeaders.ToEnvelope(result, channel);

            // Kafka does not count delivery attempts - it has no concept of one - so the adapter
            // counts. Without this a poison message loops forever, because the pipeline above only
            // gives up when it is TOLD which attempt it is on.
            int attempt = 0;

            while (cancellationToken.IsCancellationRequested == false)
            {
                attempt = attempt + 1;
                envelope.Attempt = attempt;

                try
                {
                    handler(envelope, cancellationToken).GetAwaiter().GetResult();

                    // Only now: the fact has been processed, so losing the offset from here on
                    // costs a redelivery, which the consumer side is built to absorb.
                    consumer.Commit(result);
                    return;
                }
                catch (Exception failure)
                {
                    // The offset is NOT committed, so the retry is in place and the partition keeps
                    // its order. That means a message nobody can process stalls its partition - and
                    // that is the intended behaviour, not an oversight. The pipeline above
                    // dead-letters after its configured attempts and then returns normally; if it
                    // never does, something is wrong that a growing lag and these errors should
                    // make somebody look at. Dropping the message to keep moving would trade a
                    // visible stall for silent data loss.
                    if (attempt == 1 || attempt % _options.LogEveryNthFailure == 0)
                    {
                        _logger.LogError(failure,
                            "Delivering {SchemaId} {EventId} from {Topic}[{Partition}]@{Offset} failed on attempt {Attempt}. The offset is not committed, so this partition is not moving.",
                            envelope.SchemaId, envelope.EventId, result.Topic, result.Partition.Value, result.Offset.Value, attempt);
                    }

                    // Backoff, capped: a tight retry loop against a broken downstream is a denial of
                    // service aimed at oneself.
                    var wait = TimeSpan.FromMilliseconds(Math.Min(
                        _options.RetryBackoff.TotalMilliseconds * attempt,
                        _options.MaxRetryBackoff.TotalMilliseconds));

                    cancellationToken.WaitHandle.WaitOne(wait);
                }
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();

            // Wait for the pumps to leave their poll loops and close their own consumers. Disposing
            // a consumer out from under a polling thread is what crashes the process, so shutdown
            // waits rather than races.
            foreach (var pump in _pumps)
            {
                if (pump.Join(_shutdownTimeout) == false)
                    _logger.LogWarning("The consumer thread '{Thread}' did not stop within {Timeout}s.", pump.Name, _shutdownTimeout.TotalSeconds);
            }

            _producer?.Flush(TimeSpan.FromSeconds(5));
            _producer?.Dispose();
            _stopping.Dispose();
        }

        private static readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// The envelope on the wire, as Kafka headers.
    ///
    /// Header names are prefixed and lower-case: a header namespace is shared with every library
    /// that writes to the same topic, and casing is not something to rely on across clients.
    /// </summary>
    internal static class KafkaEnvelopeHeaders
    {
        public const string EventId = "sk-event-id";
        public const string SchemaId = "sk-schema-id";
        public const string OccurredAt = "sk-occurred-at";
        public const string CorrelationId = "sk-correlation-id";
        public const string CausationId = "sk-causation-id";
        public const string TenantId = "sk-tenant-id";
        public const string Source = "sk-source";
        public const string ContentType = "sk-content-type";

        public static Headers From(EventEnvelope envelope)
        {
            var headers = new Headers();

            void Add(string name, string value)
            {
                if (string.IsNullOrEmpty(value) == false)
                    headers.Add(name, Encoding.UTF8.GetBytes(value));
            }

            Add(EventId, envelope.EventId);
            Add(SchemaId, envelope.SchemaId);
            // Round-trip exact: "O" keeps the offset, so a consumer in another timezone reads the
            // same instant rather than a plausible-looking different one.
            Add(OccurredAt, envelope.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
            Add(CorrelationId, envelope.CorrelationId);
            Add(CausationId, envelope.CausationId);
            Add(TenantId, envelope.TenantId);
            Add(Source, envelope.Source);
            Add(ContentType, envelope.ContentType);

            return headers;
        }

        public static EventEnvelope ToEnvelope(ConsumeResult<string, string> result, string channel)
        {
            string Get(string name)
            {
                if (result.Message.Headers != null && result.Message.Headers.TryGetLastBytes(name, out var bytes) == true)
                    return Encoding.UTF8.GetString(bytes);
                return null;
            }

            var occurredAt = Get(OccurredAt);

            return new EventEnvelope()
            {
                EventId = Get(EventId),
                SchemaId = Get(SchemaId),
                Channel = channel,
                PartitionKey = result.Message.Key,
                OccurredAt = string.IsNullOrEmpty(occurredAt) == false
                    ? DateTimeOffset.Parse(occurredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    : result.Message.Timestamp.UtcDateTime,
                CorrelationId = Get(CorrelationId),
                CausationId = Get(CausationId),
                TenantId = Get(TenantId),
                Source = Get(Source),
                Payload = result.Message.Value,
                ContentType = Get(ContentType) ?? "application/json",
            };
        }
    }
}
