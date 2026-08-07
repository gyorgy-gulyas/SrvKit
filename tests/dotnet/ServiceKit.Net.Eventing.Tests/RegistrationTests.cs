using Microsoft.Extensions.DependencyInjection;

namespace ServiceKit.Net.Eventing.Tests
{
    /// <summary>A handler the host is expected to find on its own.</summary>
    [AutoRegisterEventHandler]
    public sealed class DiscoveredHandler : IEventHandler<OrderCancelled>
    {
        public static int Calls;

        public Task Handle(EventContext context, OrderCancelled @event, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A handler that implements Handle explicitly - the compiled invoker goes through the
    /// interface, so this has to work exactly like the ordinary shape.
    /// </summary>
    [AutoRegisterEventHandler]
    public sealed class ExplicitlyImplementedHandler : IEventHandler<NotifiedCustomer>
    {
        public static int Calls;

        Task IEventHandler<NotifiedCustomer>.Handle(EventContext context, NotifiedCustomer @event, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
    }

    [TestClass]
    public class RegistrationTests
    {
        [TestMethod]
        public void The_handler_call_is_compiled_not_reflected()
        {
            // The platform does not get to know its own load in advance, so a reflective Invoke per
            // delivered event is a cost paid in somebody else's production.
            var registry = new EventSubscriptionRegistry();
            registry.Add<OrderPlacedHandler, OrderPlaced_v1>();

            var subscription = registry.For("WebShop.Sales.Order.OrderPlaced.v1").Single();
            Assert.IsNotNull(subscription.Invoke);
        }

        [TestMethod]
        public async Task The_compiled_invoker_reaches_the_handler()
        {
            OrderPlacedHandler.Reset();

            var registry = new EventSubscriptionRegistry();
            registry.Add<OrderPlacedHandler, OrderPlaced_v1>();

            var subscription = registry.For("WebShop.Sales.Order.OrderPlaced.v1").Single();
            await subscription.Invoke(new OrderPlacedHandler(), new EventContext(), new OrderPlaced_v1() { orderId = "O-1" }, CancellationToken.None);

            Assert.AreEqual(1, OrderPlacedHandler.Seen.Count);
        }

        [TestMethod]
        public async Task A_handler_that_throws_throws_its_own_exception()
        {
            // A reflective Invoke would have wrapped this in a TargetInvocationException and hidden
            // the reason the delivery failed.
            OrderPlacedHandler.Reset();
            OrderPlacedHandler.FailTimes = 1;

            var registry = new EventSubscriptionRegistry();
            registry.Add<OrderPlacedHandler, OrderPlaced_v1>();
            var subscription = registry.For("WebShop.Sales.Order.OrderPlaced.v1").Single();

            var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => subscription.Invoke(new OrderPlacedHandler(), new EventContext(), new OrderPlaced_v1(), CancellationToken.None));

            StringAssert.Contains(failure.Message, "refused this attempt");
        }

        [TestMethod]
        public void An_unknown_schema_id_costs_nothing()
        {
            var registry = new EventSubscriptionRegistry();
            registry.Add<OrderPlacedHandler, OrderPlaced_v1>();

            Assert.AreEqual(0, registry.For("nothing.listens.to.this").Length);
        }

        [TestMethod]
        public void A_subscription_added_later_still_shows_up()
        {
            // The lookup index is built on first use; adding after that must invalidate it.
            var registry = new EventSubscriptionRegistry();
            registry.Add<OrderPlacedHandler, OrderPlaced_v1>();
            Assert.AreEqual(1, registry.For("WebShop.Sales.Order.OrderPlaced.v1").Length);

            registry.Add<DiscoveredHandler, OrderCancelled>();
            Assert.AreEqual(1, registry.For("WebShop.Sales.Order.OrderCancelled").Length);
        }

        [TestMethod]
        public void A_handler_bound_to_the_wrong_event_is_refused_at_registration()
        {
            var registry = new EventSubscriptionRegistry();
            var failure = Assert.ThrowsException<ArgumentException>(
                () => registry.Add(typeof(OrderCancelled), typeof(OrderPlacedHandler)));

            StringAssert.Contains(failure.Message, "does not implement IEventHandler");
        }

        [TestMethod]
        public void Handlers_register_themselves()
        {
            // The lesson from the gRPC controllers that were generated, never mapped, and never
            // missed: a generated surface has to register itself.
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddServiceKitEventing();
            services.UseEventing_InMemory();
            services.AddEventHandlersFromAssemblies(typeof(DiscoveredHandler).Assembly);

            using var provider = services.BuildServiceProvider();
            var registry = provider.GetRequiredService<EventSubscriptionRegistry>();

            Assert.AreEqual(1, registry.For("WebShop.Sales.Order.OrderCancelled").Length);
            Assert.AreEqual(typeof(DiscoveredHandler), registry.For("WebShop.Sales.Order.OrderCancelled")[0].HandlerType);
            Assert.IsNotNull(provider.GetRequiredService<IServiceScopeFactory>().CreateScope().ServiceProvider.GetRequiredService<DiscoveredHandler>());
        }

        [TestMethod]
        public async Task An_explicitly_implemented_handler_works_the_same()
        {
            ExplicitlyImplementedHandler.Calls = 0;

            var registry = new EventSubscriptionRegistry();
            registry.Add<ExplicitlyImplementedHandler, NotifiedCustomer>();

            var subscription = registry.For("WebShop.Sales.Order.NotifiedCustomer").Single();
            await subscription.Invoke(new ExplicitlyImplementedHandler(), new EventContext(), new NotifiedCustomer(), CancellationToken.None);

            Assert.AreEqual(1, ExplicitlyImplementedHandler.Calls);
        }

        [TestMethod]
        public void Finding_no_handler_is_said_out_loud()
        {
            // A process that silently subscribed to nothing looks healthy until the first event
            // goes unhandled.
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddServiceKitEventing();
            services.AddEventHandlersFromAssemblies(typeof(string).Assembly);

            using var provider = services.BuildServiceProvider();
            var warning = provider.GetService<IStartupWarning>();

            Assert.IsNotNull(warning);
            StringAssert.Contains(warning.Message, "AutoRegisterEventHandler");
        }
    }
}
