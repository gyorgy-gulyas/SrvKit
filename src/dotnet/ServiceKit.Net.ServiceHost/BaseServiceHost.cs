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
            public string PathBase = default(string);
        }

        protected WebApplicationBuilder _builder;
        protected WebApplication _app;
        protected bool _ready = false;

        // Static factory method to create and configure the service
        public static IHost Create<TService>(string[] args, Options options) where TService : BaseServiceHost, new()
        {
            var service = new TService();

            if (options == default)
                options = new Options();

            service.AddServices(args, options);
            var host = service.Build(options);

            service._BeforeRun(host,options).Wait();

            return host;
        }

        protected abstract Task _BeforeRun(WebApplication app, Options options);

        // Register required services into the DI container based on the selected options
        private void AddServices(string[] args, Options options)
        {
            _builder = WebApplication.CreateBuilder(args);

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

        // Abstract extension points for derived classes to add additional services
        protected abstract void _BeforeAddServices(IServiceCollection services, Options options);
        protected abstract void _AfterAddServices(IServiceCollection services, Options options);

        // Build and configure the HTTP pipeline and start the application
        private WebApplication Build(Options options)
        {
            _app = _builder.Build();

            // Add health endpoints like "/" and "/live" and "/rediness"
            AddDefaultRootings();

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
