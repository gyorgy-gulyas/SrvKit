using System.Diagnostics;

namespace ServiceKit.Net
{
    /// <summary>
    /// The platform's own tracing handles.
    ///
    /// A service that wants a span of its own starts it from <see cref="ActivitySource"/>; the host
    /// registers that same name with OpenTelemetry, so anything started here is exported without the
    /// service having to know how. When no tracer is listening an ActivitySource returns null and
    /// costs nothing, which is why calling it unconditionally is fine.
    /// </summary>
    public static class ServiceKitDiagnostics
    {
        public const string ActivitySourceName = "ServiceKit.Net";

        public static readonly ActivitySource ActivitySource = new ActivitySource(
            ActivitySourceName,
            typeof(ServiceKitDiagnostics).Assembly.GetName().Version?.ToString());

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
