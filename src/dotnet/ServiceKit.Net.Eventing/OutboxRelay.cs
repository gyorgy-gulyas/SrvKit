using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// The courier. It reads what the commit left behind and gets it out.
    ///
    /// This is the least clever piece of the design and that is deliberate: take the oldest
    /// undelivered envelopes, hand them to the broker, mark them delivered. If the broker refuses,
    /// do not mark them and try again later. If the process dies after the broker accepted but
    /// before the mark, the envelope goes out twice - which is precisely the case the inbox on the
    /// consumer side exists to absorb.
    ///
    /// It runs in the service process by default. A deployment that would rather have one dedicated
    /// drainer can turn it off here and run it elsewhere; the recording side does not change,
    /// because the intent was written down rather than sent.
    /// </summary>
    public sealed class OutboxRelay : BackgroundService
    {
        private readonly IOutboxStore _outbox;
        private readonly IEventBroker _broker;
        private readonly EventingOptions _options;
        private readonly ILogger<OutboxRelay> _logger;

        public OutboxRelay(IOutboxStore outbox, IEventBroker broker, IOptions<EventingOptions> options, ILogger<OutboxRelay> logger)
        {
            _outbox = outbox;
            _broker = broker;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_options.RunRelay == false)
            {
                _logger.LogInformation("The outbox relay is disabled in this process; something else is expected to drain the outbox.");
                return;
            }

            while (stoppingToken.IsCancellationRequested == false)
            {
                int published = 0;
                try
                {
                    published = await RunOnce(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested == true)
                {
                    break;
                }
                catch (Exception failure)
                {
                    // The loop must not die. An outbox that stops being drained is a system that
                    // has quietly stopped telling anyone anything.
                    _logger.LogError(failure, "The outbox relay pass failed; retrying after the idle delay.");
                }

                if (published == 0)
                    await Task.Delay(_options.RelayIdleDelay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }

        /// <summary>
        /// One pass. Public so a test can drive the relay deterministically instead of waiting for
        /// a timer - a test that sleeps is a test that is flaky on someone else's machine.
        /// </summary>
        public async Task<int> RunOnce(CancellationToken cancellationToken = default)
        {
            var batch = await _outbox.ReadUnsent(_options.RelayBatchSize, cancellationToken);
            if (batch.Count == 0)
                return 0;

            var sent = new List<string>(batch.Count);

            foreach (var envelope in batch)
            {
                try
                {
                    await _broker.Publish(envelope, cancellationToken);
                    sent.Add(envelope.EventId);

                    EventingDiagnostics.Published.Add(1, new KeyValuePair<string, object>("schema_id", envelope.SchemaId));
                    EventingDiagnostics.RelayLagSeconds.Record((DateTimeOffset.UtcNow - envelope.OccurredAt).TotalSeconds);
                }
                catch (Exception failure)
                {
                    await _outbox.MarkAttemptFailed(envelope.EventId, failure.Message, CancellationToken.None);
                    _logger.LogWarning(failure, "Publishing {SchemaId} {EventId} failed; it stays in the outbox.", envelope.SchemaId, envelope.EventId);

                    // Stop the pass here rather than skipping ahead: within a partition key the
                    // order is a promise, and stepping over a stuck envelope breaks it.
                    break;
                }
            }

            if (sent.Count > 0)
                await _outbox.MarkSent(sent, cancellationToken);

            return sent.Count;
        }
    }
}
