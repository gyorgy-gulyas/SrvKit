using System.Linq.Expressions;
using System.Reflection;

namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// One handler bound to one schema id, with the call already compiled.
    /// </summary>
    public sealed class EventSubscription
    {
        public string SchemaId { get; init; }
        public string Channel { get; init; }
        public Type EventType { get; init; }
        public Type HandlerType { get; init; }

        /// <summary>
        /// The handler call, compiled once at registration.
        ///
        /// This is not premature: the platform does not get to know its own load in advance - it is
        /// meant to be used in a lot of places - so a MethodInfo lookup and a reflective Invoke per
        /// delivered event is a cost that would show up in somebody else's production, not in ours.
        /// Registration happens once; delivery happens forever.
        /// </summary>
        public Func<object, EventContext, object, CancellationToken, Task> Invoke { get; init; }
    }

    /// <summary>
    /// Who listens to what, in this process.
    ///
    /// Filled at registration - by generated code, not by hand. Dispatch matches on the schema id
    /// alone, which is why a subscriber never needs to reference the producing service's contract:
    /// it needs the event type and nothing else.
    /// </summary>
    public sealed class EventSubscriptionRegistry
    {
        private static readonly EventSubscription[] _none = Array.Empty<EventSubscription>();

        private readonly List<EventSubscription> _subscriptions = new();
        private Dictionary<string, EventSubscription[]> _bySchemaId;

        public IReadOnlyList<EventSubscription> Subscriptions => _subscriptions;

        /// <summary>Every distinct channel something in this process listens to.</summary>
        public IReadOnlyList<string> Channels =>
            _subscriptions.Select(s => s.Channel).Distinct(StringComparer.Ordinal).ToArray();

        /// <summary>
        /// The handlers for a schema id. A dictionary lookup returning a pre-built array - no
        /// filtering and no allocation on the delivery path.
        /// </summary>
        public EventSubscription[] For(string schemaId)
        {
            var index = _bySchemaId ??= Build();
            return index.TryGetValue(schemaId, out var found) ? found : _none;
        }

        private Dictionary<string, EventSubscription[]> Build()
        {
            return _subscriptions
                .GroupBy(s => s.SchemaId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        }

        public void Add<THandler, TEvent>()
            where THandler : class, IEventHandler<TEvent>
            where TEvent : class, IDomainEvent, new()
        {
            Add(typeof(TEvent), typeof(THandler));
        }

        public void Add(Type eventType, Type handlerType)
        {
            if (typeof(IDomainEvent).IsAssignableFrom(eventType) == false)
                throw new ArgumentException($"'{eventType.FullName}' is not an event: it does not implement IDomainEvent.", nameof(eventType));

            var handlerInterface = typeof(IEventHandler<>).MakeGenericType(eventType);
            if (handlerInterface.IsAssignableFrom(handlerType) == false)
                throw new ArgumentException($"'{handlerType.FullName}' does not implement IEventHandler<{eventType.Name}>.", nameof(handlerType));

            // The schema id and channel are instance members because a generated event carries them
            // as ordinary properties - so one throwaway instance is created here, once, at
            // registration, to read them. Cheap, and it keeps the generated shape simple.
            var probe = (IDomainEvent)Activator.CreateInstance(eventType);

            _subscriptions.Add(new EventSubscription()
            {
                SchemaId = probe.SchemaId,
                Channel = probe.Channel,
                EventType = eventType,
                HandlerType = handlerType,
                Invoke = CompileInvoker(eventType, handlerType, handlerInterface),
            });

            // A subscription added after the first dispatch has to show up in the index too.
            _bySchemaId = null;
        }

        /// <summary>
        /// Builds (handler, context, payload, ct) =&gt; ((THandler)handler).Handle(context, (TEvent)payload, ct)
        /// and compiles it. Reflection runs here, at startup, and never again.
        /// </summary>
        private static Func<object, EventContext, object, CancellationToken, Task> CompileInvoker(Type eventType, Type handlerType, Type handlerInterface)
        {
            var handleMethod = handlerInterface.GetMethod(nameof(IEventHandler<IDomainEvent>.Handle))
                ?? throw new InvalidOperationException($"IEventHandler<{eventType.Name}> has no Handle method.");

            var handler = Expression.Parameter(typeof(object), "handler");
            var context = Expression.Parameter(typeof(EventContext), "context");
            var payload = Expression.Parameter(typeof(object), "payload");
            var cancellation = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

            // Called through the interface, so a handler that implements Handle explicitly works
            // exactly like one that does not.
            var call = Expression.Call(
                Expression.Convert(handler, handlerInterface),
                handleMethod,
                context,
                Expression.Convert(payload, eventType),
                cancellation);

            return Expression
                .Lambda<Func<object, EventContext, object, CancellationToken, Task>>(call, handler, context, payload, cancellation)
                .Compile();
        }
    }

    /// <summary>
    /// Marks a generated handler so the host finds it without anyone wiring it up.
    ///
    /// The same shape as [AutoRegisterGrpc], and for the same reason: this platform once generated a
    /// complete gRPC surface that was never mapped, and nothing complained - a method that is merely
    /// never called looks exactly like a healthy one. A generated surface has to register itself.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class AutoRegisterEventHandlerAttribute : Attribute
    {
    }
}
