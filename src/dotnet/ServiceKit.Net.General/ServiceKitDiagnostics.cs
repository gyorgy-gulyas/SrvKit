using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ServiceKit.Net
{
    /// <summary>
    /// The platform's own tracing and metric handles.
    ///
    /// A service that wants a span or a counter of its own takes it from here; the host registers
    /// these same names with OpenTelemetry, so anything created here is exported without the service
    /// having to know how. When nothing is listening an ActivitySource returns null and an
    /// instrument records into nowhere, which is why calling them unconditionally is fine.
    /// </summary>
    public static class ServiceKitDiagnostics
    {
        public const string ActivitySourceName = "ServiceKit.Net";
        public const string MeterName = "ServiceKit.Net";

        private static readonly string _version = typeof(ServiceKitDiagnostics).Assembly.GetName().Version?.ToString();

        public static readonly ActivitySource ActivitySource = new ActivitySource(ActivitySourceName, _version);

        /// <summary>
        /// Where a service's own counters and histograms belong. Instruments are created once and
        /// kept - a Meter is not something to build per request.
        /// </summary>
        public static readonly Meter Meter = new Meter(MeterName, _version);

        // The call's identity, put on the span so a trace can be searched by the same things the
        // logs are searched by. Prefixed because a tag name is a global namespace shared with every
        // library that writes to the same trace.
        public const string tag_correlation_id = "servicekit.correlation_id";
        public const string tag_call_stack = "servicekit.call_stack";
        public const string tag_tenant_id = "servicekit.tenant_id";
        public const string tag_identity_id = "servicekit.identity_id";
        public const string tag_client_application = "servicekit.client_application";
    }
}
