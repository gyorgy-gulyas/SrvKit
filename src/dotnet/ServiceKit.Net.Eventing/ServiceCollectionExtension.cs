using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceKit.Net.Eventing.InMemory;

namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// Registration. Which broker and which stores carry the facts is a deployment decision, so it
    /// is made here - once - and nothing downstream knows which one it got.
    /// </summary>
    public static class ServiceCollectionExtension
    {
        /// <summary>
        /// The parts that are the same whatever carries the events.
        /// </summary>
        public static IServiceCollection AddServiceKitEventing(this IServiceCollection services, Action<EventingOptions> configure = null)
        {
            if (configure != null)
                services.Configure(configure);
            else
                services.Configure<EventingOptions>(_ => { });

            services.TryAddSingleton<IEventSerializer, JsonEventSerializer>();
            services.TryAddSingleton<EventSubscriptionRegistry>();
            services.TryAddSingleton<IEventDispatcher, EventDispatcher>();

            // Scoped, not singleton: a recorder is a unit of work's pending list. Two requests
            // sharing one would hand each other's facts to whichever saved first.
            services.TryAddScoped<EventRecordingContext>();
            services.TryAddScoped<IEventRecorder, EventRecorder>();

            services.AddHostedService<OutboxRelay>();
            services.AddHostedService<EventSubscriberHost>();

            return services;
        }

        /// <summary>
        /// Everything in this process: outbox, inbox, broker and dead letters.
        ///
        /// This is the test and single-process-development setup, and it is a first-class one - the
        /// contract test takes the same path as production, minus the network.
        /// </summary>
        public static IServiceCollection UseEventing_InMemory(this IServiceCollection services)
        {
            services.TryAddSingleton<InMemoryOutboxStore>();
            services.TryAddSingleton<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());

            services.TryAddSingleton<InMemoryDeadLetterSink>();
            services.TryAddSingleton<IDeadLetterSink>(sp => sp.GetRequiredService<InMemoryDeadLetterSink>());

            services.TryAddSingleton<IInboxStore, InMemoryInboxStore>();
            services.TryAddSingleton<IEventBroker>(_ => new InMemoryEventBroker());

            return services;
        }

        /// <summary>
        /// Binds a handler to an event.
        ///
        /// Called by GENERATED code, not by hand. A generated surface that has to be wired up
        /// manually is a surface that silently never runs - this platform shipped that exact bug in
        /// its gRPC controllers and nobody noticed for months, because nothing complains about a
        /// method that is merely never called.
        /// </summary>
        public static IServiceCollection AddEventHandler<THandler, TEvent>(this IServiceCollection services)
            where THandler : class, IEventHandler<TEvent>
            where TEvent : class, IDomainEvent, new()
        {
            services.AddScoped<THandler>();

            // The registry is needed at registration time, not at resolve time, so it is built here
            // rather than resolved from a provider that does not exist yet.
            var registry = (EventSubscriptionRegistry)services
                .LastOrDefault(d => d.ServiceType == typeof(EventSubscriptionRegistry))?.ImplementationInstance;

            if (registry == null)
            {
                registry = new EventSubscriptionRegistry();
                services.RemoveAll<EventSubscriptionRegistry>();
                services.AddSingleton(registry);
            }

            registry.Add(typeof(TEvent), typeof(THandler));
            return services;
        }
    }
}
