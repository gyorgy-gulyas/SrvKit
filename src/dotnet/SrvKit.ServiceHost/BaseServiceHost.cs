using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using SrvKit.Net;
using Microsoft.AspNetCore.ResponseCompression;
using System.IO.Compression;

namespace ServiceKit.Net
{
    // Abstract base class for hosting services with common features like REST, gRPC, authentication, and health checks
    public abstract class BaseServiceHost
    {
        // Options class to toggle optional features like authentication, REST, and gRPC
        public class Options
        {
            public bool WithAuthentication = true;
            public bool WithGrpc = true;
            public bool WithRest = true;
            public bool WithReponseCompression = true;
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
            return service.Build(options);
        }

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
                _builder.Services.AddControllers();

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

            // Configure CORS policy
            _builder.Services.AddCors(options =>
            {
                options.AddPolicy("cors_policy", policy =>
                {
                    policy
                        .AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader();
                });
            });

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

            _BeforeBuild(_builder.Services, options);

            _app.UseCors("cors_policy");

            if (_app.Environment.IsDevelopment())
            {
                _app.UseSwagger();
                _app.UseSwaggerUI();
            }

            if (options.WithAuthentication)
            {
                _app.UseAuthentication();
                _app.UseAuthorization();
            }

            // Register REST and gRPC endpoints
            _app.MapControllers();
            _app.MapGrpcControllers();

            _AfterBuild(_builder.Services, options);

            _ready = true;
            return _app;
        }

        // Abstract hooks to let subclasses hook into build process
        protected abstract void _BeforeBuild(IServiceCollection services, Options options);
        protected abstract void _AfterBuild(IServiceCollection services, Options options);

        // Define default root endpoints: "/" and "/live"
        private void AddDefaultRootings()
        {
            _app.MapGet("/", () => "Service is running!");
            _app.MapGet("/rediness", () =>
            {
                if (_ready == false)
                    return Results.StatusCode(500);

                return Results.Ok("ready");
            });

            _app.MapGet("/live", async () =>
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
