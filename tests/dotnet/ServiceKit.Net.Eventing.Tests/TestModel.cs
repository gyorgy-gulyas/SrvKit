using ServiceKit.Net.Eventing;

namespace ServiceKit.Net.Eventing.Tests
{
    /// <summary>
    /// Stands in for what the .NET emitter will generate: an ordinary data class whose only extra
    /// members are the two routing constants.
    /// </summary>
    public sealed class OrderPlaced_v1 : IDomainEvent
    {
        public string SchemaId => "WebShop.Sales.Order.OrderPlaced.v1";
        public string Channel => "WebShop.Sales";

        public string orderId { get; set; }
        public decimal totalPrice { get; set; }
    }

    public sealed class OrderCancelled : IDomainEvent
    {
        // No version: an internal fact whose consumers ship in the same unit and move with it.
        public string SchemaId => "WebShop.Sales.Order.OrderCancelled";
        public string Channel => "WebShop.Sales";

        public string orderId { get; set; }
        public string reason { get; set; }
    }

    /// <summary>Nothing in the test process listens to this one.</summary>
    public sealed class NobodyListens : IDomainEvent
    {
        public string SchemaId => "WebShop.Sales.Order.NobodyListens";
        public string Channel => "WebShop.Sales";
    }

    /// <summary>
    /// A fact whose business data happens to be called 'channel'. The routing constants are dropped
    /// from the payload by matching the interface implementation, not the name - so this field has
    /// to survive.
    /// </summary>
    public sealed class NotifiedCustomer : IDomainEvent
    {
        public string SchemaId => "WebShop.Sales.Order.NotifiedCustomer";
        string IDomainEvent.Channel => "WebShop.Sales";

        public string orderId { get; set; }
        public string channel { get; set; }
    }

    /// <summary>What the developer writes: the body, and nothing else.</summary>
    public sealed class OrderPlacedHandler : IEventHandler<OrderPlaced_v1>
    {
        public static readonly List<(EventContext Context, OrderPlaced_v1 Event)> Seen = new();
        public static int FailTimes;

        public Task Handle(EventContext context, OrderPlaced_v1 @event, CancellationToken cancellationToken = default)
        {
            if (FailTimes > 0)
            {
                FailTimes--;
                throw new InvalidOperationException("the handler refused this attempt");
            }

            lock (Seen) { Seen.Add((context, @event)); }
            return Task.CompletedTask;
        }

        public static void Reset()
        {
            lock (Seen) { Seen.Clear(); }
            FailTimes = 0;
        }
    }
}
