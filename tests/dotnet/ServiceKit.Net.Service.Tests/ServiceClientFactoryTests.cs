using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ServiceKit.Net.Tests
{
    // A generated client used to do new HttpClient() and GrpcChannel.ForAddress() per instance. Both
    // are the kind of mistake that works perfectly until there is traffic.
    [TestClass]
    public class ServiceClientFactoryTests
    {
        private static IServiceProvider Provider(params (string Key, string Value)[] settings)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings.ToDictionary(setting => setting.Key, setting => setting.Value))
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddServiceKitClients();

            return services.BuildServiceProvider();
        }

        [TestMethod]
        public void The_address_of_a_service_is_a_deployment_decision()
        {
            var factory = Provider(("Services:Orders:BaseAddress", "http://orders:5000"))
                .GetRequiredService<IServiceClientFactory>();

            var client = factory.CreateHttpClient("Orders");

            Assert.AreEqual(new Uri("http://orders:5000"), client.BaseAddress);
        }

        [TestMethod]
        public void A_service_with_no_address_says_so_instead_of_calling_nowhere()
        {
            var factory = Provider().GetRequiredService<IServiceClientFactory>();

            var thrown = Assert.ThrowsException<InvalidOperationException>(() => factory.CreateHttpClient("Orders"));

            StringAssert.Contains(thrown.Message, "Services:Orders:BaseAddress");
        }

        [TestMethod]
        public void The_grpc_channel_is_shared_rather_than_built_per_call_site()
        {
            // A channel owns the connection, the HTTP/2 session and the load balancing state. One
            // per call site is a connection storm.
            var factory = Provider(("Services:Orders:BaseAddress", "http://orders:5000"))
                .GetRequiredService<IServiceClientFactory>();

            Assert.AreSame(factory.GetChannel("Orders"), factory.GetChannel("Orders"));
        }

        [TestMethod]
        public void Two_names_for_one_deployment_share_the_connection()
        {
            var factory = Provider(
                    ("Services:Orders:BaseAddress", "http://sales:5000"),
                    ("Services:Invoices:BaseAddress", "http://sales:5000"))
                .GetRequiredService<IServiceClientFactory>();

            Assert.AreSame(factory.GetChannel("Orders"), factory.GetChannel("Invoices"));
        }

        [TestMethod]
        public void Grpc_falls_back_to_the_rest_address_because_tls_serves_both_on_one_port()
        {
            var factory = Provider(("Services:Orders:BaseAddress", "https://orders"))
                .GetRequiredService<IServiceClientFactory>();

            Assert.AreEqual("https://orders", factory.GrpcAddressOf("Orders"));
        }

        [TestMethod]
        public void A_cleartext_deployment_can_point_grpc_somewhere_else()
        {
            // Which is exactly what Options.GrpcPort on the host produces: REST on one port, gRPC on
            // another, because without TLS one port cannot serve both.
            var factory = Provider(
                    ("Services:Orders:BaseAddress", "http://orders:5000"),
                    ("Services:Orders:GrpcAddress", "http://orders:5001"))
                .GetRequiredService<IServiceClientFactory>();

            Assert.AreEqual("http://orders:5001", factory.GrpcAddressOf("Orders"));
        }

        [TestMethod]
        public void The_channels_are_closed_with_the_factory()
        {
            var provider = Provider(("Services:Orders:BaseAddress", "http://orders:5000"));
            var factory = provider.GetRequiredService<IServiceClientFactory>();
            factory.GetChannel("Orders");

            ((IDisposable)factory).Dispose();

            // a new one after disposal rather than a dead handle
            Assert.IsNotNull(factory.GetChannel("Orders"));
        }
    }
}
