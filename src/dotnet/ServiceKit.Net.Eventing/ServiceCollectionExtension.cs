using System.Reflection;
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
            EnsureRegistry(services).Add(typeof(TEvent), typeof(THandler));
            return services;
        }

        /// <summary>
        /// Registers every [AutoRegisterEventHandler] handler it can find, so nothing has to be
        /// wired up by hand and therefore nothing can be forgotten.
        ///
        /// With no assemblies given it searches the loaded ones that reference this library - the
        /// only ones that can carry the attribute. Finding nothing is said out loud: a process that
        /// silently subscribed to nothing looks healthy right up until the first event goes
        /// unhandled.
        /// </summary>
        public static IServiceCollection AddEventHandlersFromAssemblies(this IServiceCollection services, params Assembly[] assemblies)
        {
            var searched = (assemblies != null && assemblies.Length > 0) ? assemblies : CandidateAssemblies();

            var registry = EnsureRegistry(services);
            int found = 0;

            foreach (var type in searched.SelectMany(LoadableTypes))
            {
                if (type.IsClass == false || type.IsAbstract == true)
                    continue;
                if (type.GetCustomAttribute<AutoRegisterEventHandlerAttribute>() == null)
                    continue;

                var handled = type.GetInterfaces()
                    .Where(i => i.IsGenericType == true && i.GetGenericTypeDefinition() == typeof(IEventHandler<>))
                    .ToArray();

                if (handled.Length == 0)
                    throw new InvalidOperationException($"'{type.FullName}' is marked [AutoRegisterEventHandler] but implements no IEventHandler<>.");

                services.AddScoped(type);

                foreach (var handlerInterface in handled)
                {
                    registry.Add(handlerInterface.GetGenericArguments()[0], type);
                    found++;
                }
            }

            if (found == 0)
                services.AddSingleton<IStartupWarning>(new NoEventHandlersFound(searched));

            return services;
        }

        private static EventSubscriptionRegistry EnsureRegistry(IServiceCollection services)
        {
            var registry = (EventSubscriptionRegistry)services
                .LastOrDefault(d => d.ServiceType == typeof(EventSubscriptionRegistry))?.ImplementationInstance;

            if (registry == null)
            {
                registry = new EventSubscriptionRegistry();
                services.RemoveAll<EventSubscriptionRegistry>();
                services.AddSingleton(registry);
            }

            return registry;
        }

        // The entry assembly alone is not good enough: under a test run it is the test runner, and
        // the handlers live in a referenced project anyway.
        private static Assembly[] CandidateAssemblies()
        {
            var ownName = typeof(AutoRegisterEventHandlerAttribute).Assembly.GetName().Name;

            return AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(assembly =>
                    assembly.IsDynamic == false &&
                    (assembly.GetName().Name == ownName ||
                     assembly.GetReferencedAssemblies().Any(referenced => referenced.Name == ownName)))
                .ToArray();
        }

        // One unloadable type must not take the host down at startup; the types that did load are
        // still worth registering.
        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException failure)
            {
                return failure.Types.Where(type => type != null);
            }
        }
    }

    /// <summary>Something worth saying at startup, once the logger exists.</summary>
    public interface IStartupWarning
    {
        string Message { get; }
    }

    internal sealed class NoEventHandlersFound : IStartupWarning
    {
        public NoEventHandlersFound(Assembly[] searched)
        {
            Message = $"No [AutoRegisterEventHandler] handler was found in {searched.Length} assemblies ({string.Join(", ", searched.Select(a => a.GetName().Name))}). If this process is meant to consume events, pass the assembly explicitly.";
        }

        public string Message { get; }
    }
}
