using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace ServiceKit.Net.Tests
{
    // A real host, started in process on a free port. The BaseServiceHost is abstract and starts
    // through a static factory, so this is the smallest thing that can be called a host - and
    // starting it for real is the point: what these tests check (the pipeline order, the log
    // enrichment, the health endpoints) only exists once the pipeline is built.
    public sealed class TestServiceHost : BaseServiceHost
    {
        // The sink is picked up from the container by ReadFrom.Services, which is how a test gets to
        // see what the host actually logged without touching the static Log.Logger.
        public static readonly CapturingSink Sink = new CapturingSink();

        public static BaseServiceHost.Options DefaultOptions => new()
        {
            WithAuthentication = false,
            WithGrpc = false,
            WithRest = true,
            WithReponseCompression = false,
        };

        public static async Task<(IHost Host, string Address)> Start(BaseServiceHost.Options options = null, params string[] extraArgs)
        {
            var port = _FreePort();
            var args = new List<string> { "--urls", $"http://127.0.0.1:{port}", "--applicationName", "ServiceKit.Net.ServiceHost.Tests" };
            args.AddRange(extraArgs);

            var host = BaseServiceHost.Create<TestServiceHost>(args.ToArray(), options ?? DefaultOptions);
            await host.StartAsync();

            return (host, $"http://127.0.0.1:{port}");
        }

        // The spans the host exported. AddOpenTelemetry is additive, so the test hangs its own
        // exporter off the very provider the host configured rather than building a second one.
        public static readonly List<Activity> Spans = new List<Activity>();

        // An instrument is created once and kept; building one per request would create a new time
        // series every time.
        private static readonly System.Diagnostics.Metrics.Counter<long> _thingsCounted =
            ServiceKitDiagnostics.Meter.CreateCounter<long>("things_counted");

        protected override void _BeforeAddServices(IServiceCollection services, Options options)
        {
            services.AddSingleton<ILogEventSink>(Sink);

            if (options.WithTracing == true)
                services.AddOpenTelemetry().WithTracing(tracing => tracing.AddInMemoryExporter(Spans));
        }

        protected override void _AfterAddServices(IServiceCollection services, Options options)
        {
        }

        protected override void _BeforeBuild(WebApplication app, Options options)
        {
        }

        protected override void _AfterBuild(WebApplication app, Options options)
        {
            // something to log from, inside the request pipeline
            app.MapGet("/say-something", (Microsoft.Extensions.Logging.ILogger<TestServiceHost> logger) =>
            {
                logger.LogInformation("something happened");
                return Results.Ok("said");
            });

            // a service counting something of its own, through the platform's Meter
            app.MapGet("/count-something", () =>
            {
                _thingsCounted.Add(1);
                return Results.Ok("counted");
            });

            // something that fails, so the status code shows up as a dimension
            app.MapGet("/blow-up", () => Results.StatusCode(500));

            // a service starting a span of its own, through the platform's ActivitySource
            app.MapGet("/do-some-work", () =>
            {
                using (var activity = ServiceKitDiagnostics.ActivitySource.StartActivity("some work"))
                {
                    activity?.SetTag("work.items", 42);
                    return Results.Ok("worked");
                }
            });

            // what a generated controller would see when it builds its CallingContext
            app.MapGet("/what-do-you-see", (Microsoft.AspNetCore.Http.HttpContext http, Microsoft.Extensions.Logging.ILogger<TestServiceHost> logger) =>
            {
                var ctx = CallingContext.FromHttpContext(http, logger);
                return Results.Text(ctx.CorrelationId);
            });
        }

        // Set to make _BeforeRun throw, so a test can watch what a failed startup reports.
        public static Exception BeforeRunThrows;

        // Yields on purpose: the point of CreateAsync is that a hook which really suspends is
        // awaited rather than blocked on.
        protected override async Task _BeforeRun(WebApplication app, Options options)
        {
            await Task.Yield();

            if (BeforeRunThrows != null)
                throw BeforeRunThrows;
        }

        private static int _FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    public sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new List<LogEvent>();
        private readonly object _lock = new object();

        public void Emit(LogEvent logEvent)
        {
            lock (_lock)
                _events.Add(logEvent);
        }

        public void Clear()
        {
            lock (_lock)
                _events.Clear();
        }

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_lock)
                    return _events.ToList();
            }
        }

        public IReadOnlyList<LogEvent> WithMessageContaining(string fragment)
        {
            return Events.Where(e => e.RenderMessage().Contains(fragment)).ToList();
        }

        public static string PropertyOf(LogEvent logEvent, string name)
        {
            if (logEvent.Properties.TryGetValue(name, out var value) == false)
                return null;

            return value is ScalarValue scalar ? scalar.Value?.ToString() : value.ToString();
        }
    }
}
