using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;

namespace ServiceKit.Net
{
    // Which service, which instance, which environment - the same answer for the traces and for the
    // metrics, because they are two views of one process and a collector joins them by exactly this.
    //
    // Configuring it from both places is deliberate: either half can be switched off, and whichever
    // is left still has to say where it came from. Applying it twice sets the same attributes twice.
    internal static class ObservabilityResource
    {
        internal static void Configure(WebApplicationBuilder builder)
        {
            builder.Services
                .AddOpenTelemetry()
                .ConfigureResource(resource => resource
                    .AddService(
                        serviceName: builder.Environment.ApplicationName,
                        serviceInstanceId: Environment.MachineName)
                    .AddAttributes(new[]
                    {
                        new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName),
                    }));
        }

        // The paths an orchestrator or a scraper hits on a timer. They are not traffic: tracing them
        // fills the trace store with noise and logging them at Information hides everything else.
        internal static bool IsBackgroundPath(string path)
        {
            if (string.IsNullOrEmpty(path) == true)
                return false;

            return path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(MetricsExtensions.DefaultPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
