namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// The transaction the outbox write joins.
    ///
    /// A marker, and deliberately empty: this library must not know what a transaction is made of.
    /// The storage adapter (PolyPersist, today) wraps its own transaction in one of these and
    /// unwraps it on the way back in. That keeps the atomicity requirement expressible here without
    /// dragging a persistence library into every service that only wants to publish a fact.
    /// </summary>
    public interface IOutboxTransaction
    {
    }

    /// <summary>
    /// Where a recorded fact waits until it has actually left.
    ///
    /// This is the piece that makes "if the commit runs, the event goes" true rather than
    /// approximately true. The gap it closes is small and always fatal: the state is committed, the
    /// process is about to write to the broker, and it dies. Without an outbox the intent to send
    /// existed only in memory, so nothing and nobody knows the fact was ever recorded.
    ///
    /// ATOMICITY: the append must join the same unit of work that saves the domain state. How
    /// strong that is depends on where the outbox lives - same store as the domain write, or a
    /// different one - and an implementation is expected to say which it is and to prove it with a
    /// test, rather than let a caller assume the stronger one.
    /// </summary>
    public interface IOutboxStore
    {
        /// <summary>
        /// Writes the envelopes as part of the caller's unit of work.
        /// </summary>
        /// <param name="transaction">
        /// The unit of work that is saving the domain state.
        ///
        /// Null means there is none - a fact recorded on its own, with no state being saved
        /// alongside it. That is legitimate and the write goes straight in: there is nothing to be
        /// atomic WITH. What this library cannot detect is a caller that HAS state to save and
        /// forgot to pass the transaction, which is why the repository is the one place that
        /// appends.
        /// </param>
        Task Append(IReadOnlyList<EventEnvelope> envelopes, IOutboxTransaction transaction, CancellationToken cancellationToken = default);

        /// <summary>
        /// The oldest not-yet-delivered envelopes, in recording order. Order matters within a
        /// partition key; across keys the relay is free to interleave.
        /// </summary>
        Task<IReadOnlyList<EventEnvelope>> ReadUnsent(int maxCount, CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks envelopes as delivered. If the process dies between the broker accepting them and
        /// this call, they are delivered again - which is exactly why the consumer side dedups.
        /// </summary>
        Task MarkSent(IReadOnlyList<string> eventIds, CancellationToken cancellationToken = default);

        /// <summary>
        /// Records a failed delivery attempt so the relay can back off and the operator can see it.
        /// </summary>
        Task MarkAttemptFailed(string eventId, string reason, CancellationToken cancellationToken = default);
    }
}
