using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ServiceKit.Net
{
    /// <summary>
    /// Distributed tracing for a service host.
    ///
    /// The point is not the exporter - it is that one call through several services is ONE thing
    /// with one identity. .NET already propagates W3C traceparent across HTTP and gRPC on its own;
    /// what was missing was a tracer that samples those spans, a source the platform's own code can
    /// start spans from, and the tie between a span and the correlation id the logs are searched by
    /// (see UseServiceKitCallIdentity).
    ///
    /// It needs no infrastructure. With no endpoint configured nothing is exported, spans are still
    /// created and still carry their trace id into every log line - so a service runs, and traces
    /// locally, with nothing installed. Point it at a collector and the same spans leave the
    /// process.
    /// </summary>
    public static class TracingExtensions
    {
        // The standard variable an OpenTelemetry collector is pointed at, plus a configuration key
        // for hosts that keep everything in appsettings.
        public const string ConfigurationKey = "Otlp:Endpoint";
        public const string EnvironmentVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

        public static void AddServiceKitTracing(this WebApplicationBuilder builder)
        {
            var endpoint = builder.Configuration[ConfigurationKey];
            if (string.IsNullOrWhiteSpace(endpoint) == true)
                endpoint = builder.Configuration[EnvironmentVariable];

            builder.Services
                .AddOpenTelemetry()
                .ConfigureResource(resource => resource
                    .AddService(
                        serviceName: builder.Environment.ApplicationName,
                        serviceInstanceId: Environment.MachineName)
                    .AddAttributes(new[]
                    {
                        new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName),
                    }))
                .WithTracing(tracing =>
                {
                    tracing
                        // spans the platform and the services start themselves
                        .AddSource(ServiceKitDiagnostics.ActivitySourceName)
                        // incoming REST and gRPC: both arrive through ASP.NET Core
                        .AddAspNetCoreInstrumentation(options =>
                        {
                            // the orchestrator calls these every few seconds and they are not
                            // traffic anybody wants to look at
                            options.Filter = context => context.Request.Path.StartsWithSegments("/health") == false;
                        })
                        // outgoing calls, which is what makes the trace span more than one service.
                        // The generated gRPC clients are covered by this too - a gRPC call is an
                        // HTTP/2 request, so the traceparent goes out on it either way.
                        .AddHttpClientInstrumentation();

                    if (string.IsNullOrWhiteSpace(endpoint) == false)
                        tracing.AddOtlpExporter(options => options.Endpoint = new Uri(endpoint));
                });
        }
    }
}
