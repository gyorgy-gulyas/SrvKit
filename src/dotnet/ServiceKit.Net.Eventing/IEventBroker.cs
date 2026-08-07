namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// The whole surface a broker has to implement. Three things, and nothing about Kafka.
    ///
    /// It fits Kafka (channel to topic, partition key to partition, consumer group to consumer
    /// group), RabbitMQ (exchange plus routing key, queue per consumer group), Azure Service Bus,
    /// and a house-built one. It also fits an in-memory implementation that runs in the test
    /// process - and that is not a convenience, it is the point: the contract test must take the
    /// same path in a unit test as it does in production.
    ///
    /// The broker is NEVER the source of truth. Kafka writes a log to disk, but that is Kafka's own
    /// business: nothing here reads it back as a store. What survives a broker being wiped is the
    /// outbox and the event store, and that is what actually makes a broker replaceable - not this
    /// interface.
    /// </summary>
    public interface IEventBroker
    {
        /// <summary>
        /// Hands the envelope over for delivery. Returning means the broker has accepted it, not
        /// that anyone has processed it.
        /// </summary>
        Task Publish(EventEnvelope envelope, CancellationToken cancellationToken = default);

        /// <summary>
        /// Starts delivering the channel's envelopes to <paramref name="handler"/>.
        /// </summary>
        /// <param name="consumerGroup">
        /// Who is listening. Two processes in the same group share the work; two different groups
        /// each get everything. This is the one piece of broker vocabulary that has to be here,
        /// because it changes what the subscriber receives.
        /// </param>
        /// <param name="handler">
        /// Returning normally is an ack. Throwing is a nack: the broker is expected to redeliver,
        /// and the delivery pipeline decides when to give up and dead-letter.
        /// </param>
        Task Subscribe(string channel, string consumerGroup, Func<EventEnvelope, CancellationToken, Task> handler, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Where an envelope goes when it cannot be processed.
    ///
    /// A failure that disappears is worse than one that stops the line, because nobody finds out
    /// until the numbers do not add up weeks later. Whatever ends up here must be visible and
    /// replayable.
    /// </summary>
    public interface IDeadLetterSink
    {
        Task Send(EventEnvelope envelope, string reason, CancellationToken cancellationToken = default);
    }
}
