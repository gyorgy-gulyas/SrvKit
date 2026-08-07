namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// A fact that has already happened.
    ///
    /// The two members here are generated constants, never hand-written: they are what lets the
    /// platform route and recognise a fact without opening its payload. Everything else about an
    /// event - what it means, what it carries - is the model's business, not this library's.
    /// </summary>
    public interface IDomainEvent
    {
        /// <summary>
        /// The stable identity of this shape on the wire, e.g. "WebShop.Orders.Order.OrderPlaced.v1".
        ///
        /// A subscriber matches on this and nothing else. It is the reason no external schema
        /// registry is required - and the reason one can be added later without changing anything
        /// here, since the id is already the key.
        /// </summary>
        string SchemaId { get; }

        /// <summary>
        /// The LOGICAL channel this fact travels on - normally the producing context.
        ///
        /// Which topic, queue, exchange or partition count that becomes is a deployment decision
        /// made in the broker adapter's configuration. The model never says "Kafka topic"; it says
        /// which conversation the fact belongs to.
        /// </summary>
        string Channel { get; }
    }
}
