namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// Something the platform can carry: it has a shape the wire can recognise and a channel to
    /// travel on.
    ///
    /// It exists so the pipe - envelope, outbox, relay - has ONE shape to work with. It is not a
    /// facade anybody uses directly: what a caller reaches for is IDomainEvent or IAuditFact, and
    /// those two are not interchangeable on purpose.
    /// </summary>
    public interface IRecordableFact
    {
        /// <summary>The stable identity of this shape on the wire. Generated; never hand-written.</summary>
        string SchemaId { get; }

        /// <summary>The logical channel it travels on. Deployment maps it to a topic or a queue.</summary>
        string Channel { get; }
    }

    /// <summary>
    /// A fact that has already happened.
    ///
    /// The two members here are generated constants, never hand-written: they are what lets the
    /// platform route and recognise a fact without opening its payload. Everything else about an
    /// event - what it means, what it carries - is the model's business, not this library's.
    /// </summary>
    public interface IDomainEvent : IRecordableFact
    {
    }
}
