namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// Where a fact is written down - and NOT where it is sent.
    ///
    /// There is deliberately no Publish on this interface, and that is the single decision the whole
    /// design rests on. If a caller could publish, the moment of publishing would be independent of
    /// the transaction that saved the state, and then two things can happen that must never happen:
    /// the state is saved and the fact never leaves (the system quietly lies about itself), or the
    /// fact leaves and the state was rolled back (the world reacts to something that did not
    /// happen). No amount of later work repairs an interface shaped that way.
    ///
    /// So recording is a memory operation. The repository writes the drained envelopes into the
    /// outbox as part of the same unit of work that saves the state, and from that point delivery is
    /// the platform's problem, not the caller's.
    /// </summary>
    public interface IEventRecorder
    {
        /// <summary>
        /// Writes a fact down. It is not sent here and it is not sent when this returns.
        /// </summary>
        /// <param name="event">The fact. Generated types supply their own schema id and channel.</param>
        /// <param name="partitionKey">
        /// The ordering scope - for an aggregate root, its identity. Facts sharing this key keep
        /// their order; nothing is promised between different keys.
        /// </param>
        void Record(IDomainEvent @event, string partitionKey);

        /// <summary>True when there is something waiting to be written to the outbox.</summary>
        bool HasPending { get; }

        /// <summary>
        /// Takes the pending envelopes and forgets them. Called by the repository inside the save,
        /// so that a rolled-back save does not leave facts behind for the next one to pick up.
        /// </summary>
        IReadOnlyList<EventEnvelope> Drain();
    }
}
