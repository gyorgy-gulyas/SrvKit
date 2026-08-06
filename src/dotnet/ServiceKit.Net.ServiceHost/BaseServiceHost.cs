using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Text.Json.Serialization;

namespace ServiceKit.Net
{
    // Abstract base class for hosting services with common features like REST, gRPC, authentication, and health checks
    public abstract class BaseServiceHost
    {
        // Options class to toggle optional features like authenltication, REST, and gRPC
        public class Options
        {
            public bool WithAuthentication = true;
            public bool WithGrpc = true;
            public bool WithRest = true;
            public bool WithReponseCompression = true;
            // Structured logging is on by default because the generated controllers already write
            // through Serilog's LogContext - a host that leaves it off throws their scope away.
            public bool WithStructuredLogging = true;
            // Tracing is on by default because it needs nothing installed: with no collector
            // configured the spans are created, never exported, and their trace id is what ties a
            // log line to the call it belongs to.
            public bool WithTracing = true;
            // Metrics likewise: the host serves its own scrape endpoint, so there is nothing to
            // install for a service to be measurable.
            public bool WithMetrics = true;
            // A second, HTTP/2-only port for gRPC, for hosts that run without TLS.
            //
            // Without TLS there is no ALPN, so one cleartext port cannot negotiate between HTTP/1.1
            // and HTTP/2: Kestrel answers an h2c request on a mixed endpoint with HTTP_1_1_REQUIRED,
            // and the gRPC surface is unreachable however correctly it was mapped. Set this and REST
            // keeps its port while gRPC gets one of its own. With TLS neither is needed - one port
            // serves both.
            public int? GrpcPort = null;
            public string PathBase = default(string);
        }

        protected WebApplicationBuilder _builder;
        protected WebApplication _app;
        protected bool _ready = false;

        // Build and configure the service. Prefer this over Create: _BeforeRun is the hook where a
        // host does its own asynchronous startup work - a migration, a warm-up, a first fetch - and
        // this is the only version that awaits it properly.
        public static async Task<IHost> CreateAsync<TService>(string[] args, Options options) where TService : BaseServiceHost, new()
        {
            var service = new TService();

            if (options == default)
                options = new Options();

            service.AddServices(args, options);
            var host = service.Build(options);

            await service._BeforeRun(host, options).ConfigureAwait(false);

            return host;
        }

        // The synchronous entry point, kept because a Program.cs written against it should not have
        // to change.
        //
        // GetAwaiter().GetResult() rather than Wait(): Wait() wraps whatever _BeforeRun threw in an
        // AggregateException, so a host that failed to start reported a wrapper instead of the
        // reason, and the stack trace pointed here instead of at the code that broke.
        public static IHost Create<TService>(string[] args, Options options) where TService : BaseServiceHost, new()
        {
            return CreateAsync<TService>(args, options).GetAwaiter().GetResult();
        }

        protected abstract Task _BeforeRun(WebApplication app, Options options);

        // Register required services into the DI container based on the selected options
        private void AddServices(string[] args, Options options)
        {
            _builder = WebApplication.CreateBuilder(args);

            _ConfigureCleartextGrpcEndpoint(options);

            if (options.WithStructuredLogging)
                _builder.AddServiceKitLogging();

            if (options.WithTracing)
                _builder.AddServiceKitTracing();

            if (options.WithMetrics)
                _builder.AddServiceKitMetrics();

            _BeforeAddServices(_builder.Services, options);

            if (options.WithAuthentication)
            {
                _builder.Services.AddAuthentication("Bearer");
                _builder.Services.AddAuthorization();
            }

            if (options.WithRest)
                _builder.Services.AddControllers()
                    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));


            if (options.WithGrpc)
                _builder.Services.AddGrpc();

            if (options.WithReponseCompression)
                _ConfigureCompression(_builder.Services);

            if (_builder.Environment.IsDevelopment())
            {
                // Register Swagger in development environment
                _builder.Services.AddEndpointsApiExplorer();
                _builder.Services.AddSwaggerGen();
            }

            _ConfigureCors(_builder.Services);

            _AfterAddServices(_builder.Services, options);
        }

        // Gives gRPC a port of its own when the host runs without TLS.
        //
        // It has to go through Kestrel's endpoint CONFIGURATION rather than a ConfigureKestrel
        // listener, because Kestrel takes one or the other: the moment an endpoint is declared, the
        // urls are ignored. So whatever REST was going to listen on is restated here as an endpoint
        // of its own, and the urls are cleared to keep the "overriding address(es)" warning out of
        // every startup.
        private void _ConfigureCleartextGrpcEndpoint(Options options)
        {
            if (options.WithGrpc == false || options.GrpcPort.HasValue == false)
                return;

            // Already configured by hand? Then it is not ours to rearrange.
            if (_builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any() == true)
                return;

            var urls = _builder.Configuration[WebHostDefaults.ServerUrlsKey];
            if (string.IsNullOrWhiteSpace(urls) == true)
                urls = "http://localhost:5000";

            var index = 0;
            foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                _builder.Configuration[$"Kestrel:Endpoints:ServiceKitRest{index}:Url"] = url.Trim();
                index++;
            }

            _builder.Configuration["Kestrel:Endpoints:ServiceKitGrpc:Url"] = $"http://0.0.0.0:{options.GrpcPort.Value}";
            _builder.Configuration["Kestrel:Endpoints:ServiceKitGrpc:Protocols"] = "Http2";
            _builder.Configuration[WebHostDefaults.ServerUrlsKey] = string.Empty;
        }

        // Abstract extension points for derived classes to add additional services
        protected abstract void _BeforeAddServices(IServiceCollection services, Options options);
        protected abstract void _AfterAddServices(IServiceCollection services, Options options);

        // Build and configure the HTTP pipeline and start the application
        private WebApplication Build(Options options)
        {
            _app = _builder.Build();

            // Add health endpoints like "/" and "/live" and "/rediness"
            AddDefaultRootings();

            // First in the pipeline on purpose: everything logged after this - including whatever a
            // derived host adds below, the CORS rejection and the authentication failure - belongs
            // to a request that already has a correlation id.
            if (options.WithStructuredLogging || options.WithTracing)
                _app.UseServiceKitCallIdentity();

            if (options.WithStructuredLogging)
                _app.UseServiceKitRequestLogging();

            _BeforeBuild(_app, options);

            _app.UseCors("cors_policy");

            if (_app.Environment.IsDevelopment())
            {
                _app.UseSwagger();
                _app.UseSwaggerUI();
            }


            if (string.IsNullOrEmpty(options.PathBase) == false)
            {
                _app.UsePathBase(options.PathBase);
            }

            if (options.WithAuthentication)
            {
                _app.UseAuthentication();
                _app.UseAuthorization();
            }

            // Register REST and gRPC endpoints.
            // With authentication on, the REST controllers are mapped behind RequireAuthorization -
            // a controller that really is public says so with [AllowAnonymous]. Requiring it while
            // no authentication scheme is registered would only fail every request, so a host that
            // opted out of authentication keeps the bare mapping.
            // Mapped before the controllers and deliberately outside the authentication above: a
            // scraper is not a user, and a /metrics that needs a bearer token is a /metrics nobody
            // scrapes. Keep it off the public ingress instead.
            if (options.WithMetrics)
                _app.UseServiceKitMetrics();

            if (options.WithAuthentication)
                _app.MapRestControllers();
            else
                _app.MapControllers();

            _app.MapGrpcControllers();

            _AfterBuild(_app, options);

            _ready = true;
            return _app;
        }

        // Abstract hooks to let subclasses hook into build process
        protected abstract void _BeforeBuild(WebApplication app, Options options);
        protected abstract void _AfterBuild(WebApplication app, Options options);

        // Define default root endpoints: "/" and the two health probes
        private void AddDefaultRootings()
        {
            _app.MapGet("/", () => "Service is running!");

            // Readiness answers "should traffic come here", so it may say no and be routed around.
            _app.MapGet("/health/ready", () =>
            {
                if (_ready == false)
                    return Results.StatusCode(500);

                return Results.Ok("ready");
            });

            // Liveness answers "is this process still alive", and NOTHING else.
            //
            // It used to sample CPU usage and fail over 90%. Load is not liveness: under load the
            // probe failed, the orchestrator killed the pod, its traffic moved to the remaining
            // pods, and those went over the threshold too - a busy service was turned into an
            // outage by its own health check. The sampling also blocked for half a second per
            // probe, so the check itself added to the load it was measuring.
            //
            // Answering at all is the evidence that matters: a process that can serve this has a
            // working thread pool and a working request pipeline. A restart is the only thing an
            // orchestrator can do about a failed liveness probe, and a restart does not fix load.
            // Load belongs in metrics, and shedding it belongs in readiness or in autoscaling.
            _app.MapGet("/health/live", () => Results.Ok("alive"));
        }

        // The allowed browser origins come from configuration - "Cors:AllowedOrigins", an array, or
        // the Cors__AllowedOrigins__0 style environment variables in the cluster.
        //
        // Configured origins are the good case: the policy is limited to them and may therefore also
        // carry credentials, which a wildcard never can. Left unconfigured, a development host stays
        // wide open for convenience, but anywhere else the policy allows no cross-origin request at
        // all - a browser error that is easy to read beats a service that quietly accepts everyone.
        private void _ConfigureCors(IServiceCollection services)
        {
            var allowedOrigins = _builder.Configuration
                .GetSection("Cors:AllowedOrigins")
                .GetChildren()
                .Select(child => child.Value)
                .Where(origin => string.IsNullOrWhiteSpace(origin) == false)
                .ToArray();
            var isDevelopment = _builder.Environment.IsDevelopment();

            services.AddCors(options =>
            {
                options.AddPolicy("cors_policy", policy =>
                {
                    if (allowedOrigins != null && allowedOrigins.Length > 0)
                    {
                        policy
                            .WithOrigins(allowedOrigins)
                            .AllowAnyMethod()
                            .AllowAnyHeader()
                            .AllowCredentials();
                    }
                    else if (isDevelopment == true)
                    {
                        policy
                            .AllowAnyOrigin()
                            .AllowAnyMethod()
                            .AllowAnyHeader();
                    }
                });
            });
        }

        private static void _ConfigureCompression( IServiceCollection services )
		{
			services.AddResponseCompression( options => {
				options.EnableForHttps = true;
				options.MimeTypes = new[] { "application/json" }; //#TODO: extend this!
				options.Providers.Add<BrotliCompressionProvider>();
				options.Providers.Add<GzipCompressionProvider>();
			} );

			services.Configure<BrotliCompressionProviderOptions>( options => {
				options.Level = CompressionLevel.Optimal;
			} );

			services.Configure<GzipCompressionProviderOptions>( options => {
				options.Level = CompressionLevel.Optimal;
			} );
		}
    }
}
