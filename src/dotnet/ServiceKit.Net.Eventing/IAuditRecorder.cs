namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// A piece of evidence: something that was done, kept because somebody may have to prove it
    /// later.
    ///
    /// It is deliberately NOT an <see cref="IDomainEvent"/>, and the two interfaces are deliberately
    /// not related. Nothing reacts to an audit fact - there is no handler, no retry, no
    /// compensation - and a type that could be handed to a subscriber would promise a behaviour
    /// that does not exist. Keeping them apart means the wrong use is impossible rather than merely
    /// discouraged.
    /// </summary>
    public interface IAuditFact : IRecordableFact
    {
    }

    /// <summary>
    /// The second facade: recording evidence.
    ///
    /// Same pipe underneath as a domain event - same envelope, same outbox, same relay - and a
    /// different interface on top, because the two are different acts. Announcing that something
    /// happened invites the rest of the system to respond; writing down that something was done
    /// invites nobody. One interface with two methods would make the distinction a matter of
    /// reading the method name; two interfaces make it a matter of what you can reach.
    ///
    /// The shape of the call is different anyway. A domain event is rarely "called" at all - the
    /// aggregate records it through `emits`. An audit fact has no aggregate: it is cross-cutting,
    /// and it really is called.
    /// </summary>
    public interface IAuditRecorder
    {
        /// <summary>
        /// Writes down that something was done. Like a domain event it is recorded, not sent: it
        /// leaves with the unit of work that recorded it.
        /// </summary>
        void Record(IAuditFact fact, string partitionKey);
    }

}
