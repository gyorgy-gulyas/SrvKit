namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// The consumer's memory of what it has already processed.
    ///
    /// Exactly-once delivery does not exist at a price anyone wants to pay, so the platform promises
    /// at-least-once and makes the repeat harmless. This is where the repeat is recognised: an
    /// envelope id that has been seen by this consumer group is dropped instead of processed twice.
    ///
    /// It is per consumer group, not global: two groups are two independent readers, and one having
    /// seen an event says nothing about the other.
    /// </summary>
    public interface IInboxStore
    {
        /// <summary>
        /// Records that this consumer group is processing the event, and says whether it is the
        /// first time.
        /// </summary>
        /// <returns>
        /// True when the event is new and should be processed; false when it has been seen before
        /// and must be dropped.
        /// </returns>
        Task<bool> TryBegin(string consumerGroup, string eventId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Releases a reservation made by <see cref="TryBegin"/> that did not complete, so a
        /// redelivery gets another chance. Without this a handler that crashed halfway would leave
        /// the event marked as seen and it would never be retried.
        /// </summary>
        Task Abandon(string consumerGroup, string eventId, CancellationToken cancellationToken = default);
    }
}
