using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// Turns an arriving envelope into handler calls.
    /// </summary>
    public interface IEventDispatcher
    {
        Task Dispatch(EventEnvelope envelope, CancellationToken cancellationToken = default);
    }

    public sealed class EventDispatcher : IEventDispatcher
    {
        private readonly EventSubscriptionRegistry _registry;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IEventSerializer _serializer;
        private readonly IInboxStore _inbox;
        private readonly EventingOptions _options;
        private readonly ILogger<EventDispatcher> _logger;

        public EventDispatcher(
            EventSubscriptionRegistry registry,
            IServiceScopeFactory scopeFactory,
            IEventSerializer serializer,
            IInboxStore inbox,
            IOptions<EventingOptions> options,
            ILogger<EventDispatcher> logger)
        {
            _registry = registry;
            _scopeFactory = scopeFactory;
            _serializer = serializer;
            _inbox = inbox;
            _options = options.Value;
            _logger = logger;
        }

        private async Task DrainRecordedFacts(IServiceScope scope, EventEnvelope cause)
        {
            var recorder = scope.ServiceProvider.GetService<IEventRecorder>();
            if (recorder == null || recorder.HasPending == false)
                return;

            var outbox = scope.ServiceProvider.GetService<IOutboxStore>();
            if (outbox == null)
            {
                // Nothing to write them to. Saying so beats letting the guard report them later as
                // a mystery: the facts are real, the configuration is what is missing.
                _logger.LogError("Handling {SchemaId} recorded facts, but no outbox is registered - they cannot leave.", cause.SchemaId);
                return;
            }

            // No transaction: the handler saved no state alongside these, so there is nothing to be
            // atomic with.
            await outbox.Append(recorder.Drain(), transaction: null);
        }

        public async Task Dispatch(EventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            var subscriptions = _registry.For(envelope.SchemaId);
            if (subscriptions.Length == 0)
            {
                // Not an error. A channel carries every fact of its context, and a subscriber is
                // expected to care about a few of them.
                return;
            }

            // Dedup before the handlers, not inside them: at-least-once is only workable if the
            // repeat is dropped in one place that every handler shares.
            var isNew = await _inbox.TryBegin(_options.ConsumerGroup, envelope.EventId, cancellationToken);
            if (isNew == false)
            {
                EventingDiagnostics.Duplicates.Add(1, new KeyValuePair<string, object>("schema_id", envelope.SchemaId));
                _logger.LogDebug("Event {SchemaId} {EventId} was already processed by {ConsumerGroup}; dropped.", envelope.SchemaId, envelope.EventId, _options.ConsumerGroup);
                return;
            }

            using var activity = ServiceKitDiagnostics.ActivitySource.StartActivity($"handle {envelope.SchemaId}", ActivityKind.Consumer);
            activity?.SetTag("servicekit.event.schema_id", envelope.SchemaId);
            activity?.SetTag("servicekit.event.id", envelope.EventId);
            // The payload is NOT put on the span. A fact can carry where somebody lives, and a trace
            // store is not the place for that.
            activity?.SetTag(ServiceKitDiagnostics.tag_correlation_id, envelope.CorrelationId);

            try
            {
                var context = EventContext.From(envelope);

                foreach (var subscription in subscriptions)
                {
                    var payload = _serializer.Deserialize(envelope.Payload, subscription.EventType);

                    // A scope per handler: a handler is ordinary application code and expects the
                    // same scoped services a request would give it - including a recorder of its
                    // own, so facts it records get this event as their causation.
                    using var scope = _scopeFactory.CreateScope();

                    var recordingContext = scope.ServiceProvider.GetService<EventRecordingContext>();
                    if (recordingContext != null)
                    {
                        recordingContext.CorrelationId = envelope.CorrelationId;
                        recordingContext.CausationId = envelope.EventId;
                        recordingContext.TenantId = envelope.TenantId;
                    }

                    var handler = scope.ServiceProvider.GetRequiredService(subscription.HandlerType);

                    // Compiled at registration, so nothing reflective happens per event. It also
                    // means a handler that throws throws its own exception here, rather than one
                    // wrapped in a TargetInvocationException that hides it.
                    await subscription.Invoke(handler, context, payload, cancellationToken);

                    // A handler is allowed to record facts of its own - that is how a chain of
                    // consequences forms, and why the causation id was set above. Somebody has to
                    // take them, and here it is nobody else: the handler has no repository save to
                    // hang them on unless it made one.
                    //
                    // If it DID save through a transaction with an outbox, the recorder is already
                    // empty and this is a no-op - the facts went out atomically with that state,
                    // which is better. This is the fallback for the handler that only reacts.
                    await DrainRecordedFacts(scope, envelope);
                }

                EventingDiagnostics.Handled.Add(1, new KeyValuePair<string, object>("schema_id", envelope.SchemaId));
            }
            catch (Exception failure)
            {
                // The reservation is released so a redelivery gets another chance. Leaving it in
                // place would mark a half-processed event as done and it would never come back.
                await _inbox.Abandon(_options.ConsumerGroup, envelope.EventId, CancellationToken.None);

                EventingDiagnostics.HandlerFailures.Add(1, new KeyValuePair<string, object>("schema_id", envelope.SchemaId));
                activity?.SetStatus(ActivityStatusCode.Error, failure.Message);
                _logger.LogError(failure, "Handling {SchemaId} {EventId} failed on attempt {Attempt}.", envelope.SchemaId, envelope.EventId, envelope.Attempt);
                throw;
            }
        }
    }
}
