using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
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

        // Define default root endpoints: "/" and "/live"
        private void AddDefaultRootings()
        {
            _app.MapGet("/", () => "Service is running!");
            _app.MapGet("/health/ready", () =>
            {
                if (_ready == false)
                    return Results.StatusCode(500);

                return Results.Ok("ready");
            });

            _app.MapGet("/health/live", async () =>
            {
                var cpuOverloaded = await _IsCpuOverloadedAsync();
                var threadBlocked = _IsThreadPoolBlocked();

                // If the system is overloaded or thread pool is blocked, return HTTP 500
                if (cpuOverloaded || threadBlocked)
                {
                    return Results.StatusCode(500);
                }

                return Results.Ok("alive");
            });
        }

        // Determine if the CPU usage is above a certain threshold
        private static async Task<bool> _IsCpuOverloadedAsync()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return await _GetCpuUsageWindowsAsync() > 90;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return await _GetCpuUsageLinuxAsync() > 90;
            }
            else
            {
                return false;
            }
        }

        // Windows-specific method to measure CPU usage via PerformanceCounter
        private static async Task<double> _GetCpuUsageWindowsAsync()
        {
            if (OperatingSystem.IsWindows())
            {
                using var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _ = cpuCounter.NextValue(); // Initial dummy read
                await Task.Delay(500); // Wait to get a valid sample
                return cpuCounter.NextValue();
            }
            else
            {
                return 0.0;
            }
        }

        // Linux-specific method to calculate CPU usage from /proc/stat
        private static async Task<double> _GetCpuUsageLinuxAsync()
        {
            var stat1 = await File.ReadAllLinesAsync("/proc/stat");
            var idle1 = _ParseIdle(stat1[0], out var total1);

            await Task.Delay(500);

            var stat2 = await File.ReadAllLinesAsync("/proc/stat");
            var idle2 = _ParseIdle(stat2[0], out var total2);

            var idleDelta = idle2 - idle1;
            var totalDelta = total2 - total1;

            var usage = 100.0 * (1.0 - ((double)idleDelta / totalDelta));
            return usage;

            // Helper function to extract idle and total time from /proc/stat
            long _ParseIdle(string line, out long total)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
                var idle = parts[3]; // idle is at index 3
                total = parts.Sum();
                return idle;
            }
        }

        // Check if the .NET thread pool is saturated
        private static bool _IsThreadPoolBlocked()
        {
            ThreadPool.GetAvailableThreads(out int availableWorkerThreads, out _);
            ThreadPool.GetMaxThreads(out int maxWorkerThreads, out _);

            var usedThreads = maxWorkerThreads - availableWorkerThreads;
            var usagePercent = (double)usedThreads / maxWorkerThreads * 100;

            // Return true if thread pool usage is above 90%
            return usagePercent > 90;
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
