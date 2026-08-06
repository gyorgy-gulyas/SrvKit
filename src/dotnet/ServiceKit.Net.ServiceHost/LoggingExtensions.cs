using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Context;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace ServiceKit.Net
{
    /// <summary>
    /// Structured logging for a service host: one JSON object per line, and every line of a request
    /// carrying the same correlation id, call stack, tenant and identity.
    ///
    /// The generated controllers have always pushed their scope onto Serilog's LogContext. Nothing
    /// ever configured Serilog, so those properties went nowhere and the service logged through the
    /// default console provider - a flat line per event, with no way to gather the events of one
    /// request back together.
    /// </summary>
    public static class LoggingExtensions
    {
        /// <summary>
        /// Replaces the default logging with Serilog. Configuration wins: a "Serilog" section in
        /// appsettings (or the Serilog__* environment variables) is read in full, so sinks, levels
        /// and overrides are a deployment decision. Without one, a host still logs something
        /// sensible - readable text in development, one JSON object per line anywhere else, because
        /// that is what a log collector can actually parse.
        /// </summary>
        public static void AddServiceKitLogging(this WebApplicationBuilder builder)
        {
            builder.Host.UseSerilog((context, services, configuration) =>
            {
                configuration
                    // Before ReadFrom.Configuration on purpose, so a deployment can still raise it.
                    // The framework writes four events per request of its own (starting, executing,
                    // executed, finished); UseServiceKitRequestLogging below replaces all four with
                    // one line that actually carries the request's identity - but only if the
                    // originals are quietened, otherwise both are written and the log is worse than
                    // before.
                    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    // this is what makes the per-request properties - and the scope the generated
                    // controllers push - appear on every event inside the request
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty("Service", context.HostingEnvironment.ApplicationName)
                    .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName);

                if (_HasConfiguredSink(context.Configuration) == false)
                {
                    if (context.HostingEnvironment.IsDevelopment() == true)
                        configuration.WriteTo.Console();
                    else
                        configuration.WriteTo.Console(new CompactJsonFormatter());
                }
            });
        }

        /// <summary>
        /// Establishes who the call belongs to, and hands that to BOTH the log and the trace.
        ///
        /// A request that arrives without a correlation id GETS one here. Reading the header and
        /// leaving it empty was the hole that made the whole idea useless: the first service in a
        /// chain is usually called by a browser, which sends no correlation id, so the entry point -
        /// the one request everything else descends from - was the one nobody could trace.
        ///
        /// The id it invents is the TRACE ID of the current span, not a fresh guid. That is the
        /// bridge: the log line and the distributed trace then name the call the same way, so a log
        /// search leads to a trace and back without anyone having to correlate two unrelated
        /// identifiers by hand. Only with no tracing at all does it fall back to a guid.
        ///
        /// The id is written back into the REQUEST headers as well, so the CallingContext the
        /// controllers build sees it and carries it on to the next service. That covers gRPC too:
        /// its metadata is HTTP/2 headers, and this middleware runs before either transport.
        /// </summary>
        public static void UseServiceKitCallIdentity(this WebApplication app)
        {
            app.Use(async (context, next) =>
            {
                var correlationId = _Header(context, ServiceConstans.const_correlation_id);
                if (string.IsNullOrWhiteSpace(correlationId) == true)
                {
                    correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString();
                    context.Request.Headers[ServiceConstans.const_correlation_id] = correlationId;
                }

                // answered back so a caller can quote it in a bug report without reading its own logs
                context.Response.Headers[ServiceConstans.const_correlation_id] = correlationId;

                var callStack = _Header(context, ServiceConstans.const_call_stack);
                var tenantId = _Header(context, ServiceConstans.const_tenant_id);
                var identityId = _Header(context, ServiceConstans.const_identity_id);
                var clientApplication = _Header(context, ServiceConstans.const_client_application);

                _Tag(ServiceKitDiagnostics.tag_correlation_id, correlationId);
                _Tag(ServiceKitDiagnostics.tag_call_stack, callStack);
                _Tag(ServiceKitDiagnostics.tag_tenant_id, tenantId);
                _Tag(ServiceKitDiagnostics.tag_identity_id, identityId);
                _Tag(ServiceKitDiagnostics.tag_client_application, clientApplication);

                using (LogContext.PushProperty("CorrelationId", correlationId))
                using (_PushIfPresent("CallStack", callStack))
                using (_PushIfPresent("TenantId", tenantId))
                using (_PushIfPresent("IdentityId", identityId))
                using (_PushIfPresent("ClientApplication", clientApplication))
                {
                    await next();
                }
            });
        }

        /// <summary>
        /// One line per request instead of the framework's four, carrying the properties
        /// <see cref="UseServiceKitCallIdentity"/> pushed - which is the difference between "a
        /// request failed" and "this request, from this tenant, on behalf of this identity, failed".
        /// </summary>
        public static void UseServiceKitRequestLogging(this WebApplication app)
        {
            app.UseSerilogRequestLogging(options =>
            {
                options.GetLevel = (httpContext, elapsed, exception) =>
                {
                    // the health probes are called every few seconds by the orchestrator and would
                    // otherwise drown out everything a person wants to read
                    if (httpContext.Request.Path.StartsWithSegments("/health") == true)
                        return LogEventLevel.Verbose;

                    if (exception != null || httpContext.Response.StatusCode >= 500)
                        return LogEventLevel.Error;

                    return LogEventLevel.Information;
                };
            });
        }

        private static string _Header(HttpContext context, string name)
        {
            return context.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : string.Empty;
        }

        // No-op when nothing is listening: Activity.Current is null unless a tracer asked for spans.
        private static void _Tag(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value) == true)
                return;

            Activity.Current?.SetTag(name, value);
        }

        // An empty property is worse than an absent one: it fills every line with noise and makes a
        // missing tenant look like a tenant named "".
        private static IDisposable _PushIfPresent(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value) == true)
                return _NullScope.Instance;

            return LogContext.PushProperty(name, value);
        }

        // Would the configuration produce a sink of its own? Serilog silently logs to nowhere when
        // asked to read a configuration that has no WriteTo, and a service that logs nothing looks
        // exactly like a service with nothing to say.
        private static bool _HasConfiguredSink(IConfiguration configuration)
        {
            var section = configuration.GetSection("Serilog:WriteTo");
            return section.Exists() == true && section.GetChildren().Any() == true;
        }

        private sealed class _NullScope : IDisposable
        {
            public static readonly _NullScope Instance = new _NullScope();

            private _NullScope()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
