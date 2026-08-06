using System.Collections.Concurrent;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ServiceKit.Net
{
    /// <summary>
    /// Where a client to another service comes from.
    ///
    /// Two things were missing and both bite in production. A client that does
    /// <c>new HttpClient()</c> per instance holds its connections open and never notices DNS
    /// changing - the reason IHttpClientFactory exists at all. And a gRPC channel is expensive:
    /// it owns the connection, the HTTP/2 session and the load balancing state, so one per call
    /// site is a connection storm and one per process is what everybody actually wants.
    ///
    /// Addresses come from configuration under Services:&lt;name&gt;, because which host a service
    /// answers on is a deployment decision that no generated client should carry.
    /// </summary>
    public interface IServiceClientFactory
    {
        /// <summary>An HttpClient for the named service, resilient and pointed at its address.</summary>
        HttpClient CreateHttpClient(string serviceName);

        /// <summary>The shared gRPC channel for the named service. Do not dispose it - it is shared.</summary>
        GrpcChannel GetChannel(string serviceName);

        /// <summary>The configured REST address of the named service, or null.</summary>
        string RestAddressOf(string serviceName);

        /// <summary>The configured gRPC address of the named service, or null.</summary>
        string GrpcAddressOf(string serviceName);
    }

    public sealed class ServiceClientFactory : IServiceClientFactory, IDisposable
    {
        public const string HttpClientName = "ServiceKit.ServiceClient";

        public const string ConfigurationSection = "Services";
        public const string RestAddressKey = "BaseAddress";
        public const string GrpcAddressKey = "GrpcAddress";

        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        // Ordinal: a service name is an identifier, not prose.
        private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new ConcurrentDictionary<string, GrpcChannel>(StringComparer.Ordinal);

        public ServiceClientFactory(IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        public string RestAddressOf(string serviceName)
        {
            return _configuration[$"{ConfigurationSection}:{serviceName}:{RestAddressKey}"];
        }

        public string GrpcAddressOf(string serviceName)
        {
            // Falls back to the REST address: with TLS one endpoint serves both, and only a
            // cleartext deployment needs to say otherwise (see Options.GrpcPort on the host).
            var address = _configuration[$"{ConfigurationSection}:{serviceName}:{GrpcAddressKey}"];
            return string.IsNullOrWhiteSpace(address) ? RestAddressOf(serviceName) : address;
        }

        public HttpClient CreateHttpClient(string serviceName)
        {
            var address = RestAddressOf(serviceName);
            if (string.IsNullOrWhiteSpace(address) == true)
                throw new InvalidOperationException($"Service '{serviceName}' has no address. Set {ConfigurationSection}:{serviceName}:{RestAddressKey} in configuration.");

            var client = _httpClientFactory.CreateClient(HttpClientName);
            client.BaseAddress = new Uri(address);
            return client;
        }

        public GrpcChannel GetChannel(string serviceName)
        {
            var address = GrpcAddressOf(serviceName);
            if (string.IsNullOrWhiteSpace(address) == true)
                throw new InvalidOperationException($"Service '{serviceName}' has no address. Set {ConfigurationSection}:{serviceName}:{GrpcAddressKey} in configuration.");

            // Keyed by ADDRESS rather than by name: two names pointing at one deployment should
            // share the connection, which is the whole point of caching them.
            return _channels.GetOrAdd(address, key => GrpcChannel.ForAddress(key, ResilienceExtensions.ServiceKitChannelOptions()));
        }

        public void Dispose()
        {
            foreach (var channel in _channels.Values)
                channel.Dispose();

            _channels.Clear();
        }
    }

    public static class ServiceClientExtensions
    {
        /// <summary>
        /// Registers the client factory, with the house resilience pipeline on its HttpClient.
        /// </summary>
        public static IServiceCollection AddServiceKitClients(this IServiceCollection services, Action<ResilienceExtensions.Options> configure = null)
        {
            services.AddHttpClient(ServiceClientFactory.HttpClientName)
                .AddServiceKitResilience(configure);

            services.AddSingleton<IServiceClientFactory, ServiceClientFactory>();
            return services;
        }
    }
}
