using System.Diagnostics.Metrics;

namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// What an operator needs to see about eventing, on the platform's own Meter.
    ///
    /// On ServiceKitDiagnostics.Meter deliberately: a Meter the host has not registered records into
    /// nowhere, and an instrument that silently exports nothing is worse than no instrument at all.
    /// </summary>
    public static class EventingDiagnostics
    {
        /// <summary>Facts written down. Rises with business activity, not with delivery.</summary>
        public static readonly Counter<long> Recorded =
            ServiceKitDiagnostics.Meter.CreateCounter<long>("servicekit_events_recorded_total", description: "Events recorded into the outbox.");

        /// <summary>Facts handed to the broker.</summary>
        public static readonly Counter<long> Published =
            ServiceKitDiagnostics.Meter.CreateCounter<long>("servicekit_events_published_total", description: "Events accepted by the broker.");

        /// <summary>Facts processed by a handler.</summary>
        public static readonly Counter<long> Handled =
            ServiceKitDiagnostics.Meter.CreateCounter<long>("servicekit_events_handled_total", description: "Events processed by a handler.");

        /// <summary>
        /// Redeliveries that were dropped. A steady low number is the system working as designed;
        /// a spike means something upstream is retrying hard.
        /// </summary>
        public static readonly Counter<long> Duplicates =
            ServiceKitDiagnostics.Meter.CreateCounter<long>("servicekit_events_duplicates_total", description: "Redelivered events dropped by the inbox.");

        /// <summary>Failed handler attempts - not the same as dead letters.</summary>
        public static readonly Counter<long> HandlerFailures =
            ServiceKitDiagnostics.Meter.CreateCounter<long>("servicekit_events_handler_failures_total", description: "Handler attempts that threw.");

        /// <summary>
        /// Events given up on. This is the one to alert on: everything else is the system coping,
        /// this is the system not coping.
        /// </summary>
        public static readonly Counter<long> DeadLettered =
            ServiceKitDiagnostics.Meter.CreateCounter<long>("servicekit_events_dead_lettered_total", description: "Events moved to the dead letter sink.");

        /// <summary>
        /// How long a fact waited between being recorded and leaving. The number that says whether
        /// the relay is keeping up.
        /// </summary>
        public static readonly Histogram<double> RelayLagSeconds =
            ServiceKitDiagnostics.Meter.CreateHistogram<double>("servicekit_events_relay_lag_seconds", unit: "s", description: "Seconds between recording an event and publishing it.");
    }
}
