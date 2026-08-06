using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;

namespace ServiceKit.Net
{
    /// <summary>
    /// Metrics for a service host: request rate, duration and error count, the outgoing calls it
    /// makes, and the runtime underneath it - exposed for scraping and, when a collector is
    /// configured, pushed to it as well.
    ///
    /// Metrics answer the questions a trace cannot: not "what happened to this call" but "how many,
    /// how fast, how often wrong". That is also why load belongs here and not in a liveness probe -
    /// see the note on /health/live, which used to kill healthy pods for being busy.
    ///
    /// Like the tracing, it needs nothing installed: the instruments record either way and the
    /// scrape endpoint is served by the host itself.
    /// </summary>
    public static class MetricsExtensions
    {
        public const string PathConfigurationKey = "Metrics:Path";
        public const string DefaultPath = "/metrics";

        /// <summary>
        /// How long a scrape response may be served from cache. The exporter's own default protects
        /// a service from a stampede of scrapers, which is right in a cluster and wrong in a test -
        /// where the answer to "was that request counted" must not be one taken before it happened.
        /// </summary>
        public const string ScrapeCacheConfigurationKey = "Metrics:ScrapeCacheMilliseconds";

        public static void AddServiceKitMetrics(this WebApplicationBuilder builder)
        {
            ObservabilityResource.Configure(builder);

            var endpoint = builder.Configuration[TracingExtensions.ConfigurationKey];
            if (string.IsNullOrWhiteSpace(endpoint) == true)
                endpoint = builder.Configuration[TracingExtensions.EnvironmentVariable];

            builder.Services
                .AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    metrics
                        // whatever the platform and the services count themselves
                        .AddMeter(ServiceKitDiagnostics.MeterName)
                        // requests served: rate, duration, status - REST and gRPC alike
                        .AddAspNetCoreInstrumentation()
                        // calls made, which is where a slow dependency shows up before anyone blames
                        // this service for it
                        .AddHttpClientInstrumentation()
                        // GC, thread pool, exceptions: the numbers that explain a latency graph
                        .AddRuntimeInstrumentation()
                        .AddPrometheusExporter(options =>
                        {
                            if (int.TryParse(builder.Configuration[ScrapeCacheConfigurationKey], out var cacheMilliseconds) == true)
                                options.ScrapeResponseCacheDurationMilliseconds = cacheMilliseconds;
                        });

                    if (string.IsNullOrWhiteSpace(endpoint) == false)
                        metrics.AddOtlpExporter(options => options.Endpoint = new Uri(endpoint));
                });
        }

        /// <summary>
        /// Maps the scrape endpoint - /metrics by default, "Metrics:Path" to move it.
        /// </summary>
        public static void UseServiceKitMetrics(this WebApplication app)
        {
            var path = app.Configuration[PathConfigurationKey];
            if (string.IsNullOrWhiteSpace(path) == true)
                path = DefaultPath;

            app.MapPrometheusScrapingEndpoint(path);
        }
    }
}
