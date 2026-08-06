using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;

namespace ServiceKit.Net.Tests
{
    // Without TLS there is no ALPN, so one cleartext port cannot negotiate between HTTP/1.1 and
    // HTTP/2. A service that maps its gRPC controllers correctly and listens on a single plain port
    // still answers HTTP_1_1_REQUIRED to every gRPC call - which is how a whole transport surface
    // can be unreachable while everything about it looks configured.
    [TestClass]
    public class CleartextGrpcTests
    {
        private static IHost _host;
        private static string _restAddress;
        private static int _grpcPort;

        [ClassInitialize]
        public static async Task Start(TestContext context)
        {
            _grpcPort = _FreePort();

            var options = TestServiceHost.DefaultOptions;
            options.WithGrpc = true;
            options.GrpcPort = _grpcPort;

            var started = await TestServiceHost.Start(options);
            _host = started.Host;
            _restAddress = started.Address;
        }

        [ClassCleanup]
        public static async Task Stop()
        {
            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
        }

        private static int _FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        [TestMethod]
        public async Task Rest_still_answers_on_the_port_it_was_given()
        {
            // Restating the urls as Kestrel endpoints is the part that could quietly break this:
            // declare an endpoint and Kestrel stops listening to the urls entirely.
            using var client = new HttpClient() { BaseAddress = new Uri(_restAddress) };

            var response = await client.GetAsync("/say-something");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [TestMethod]
        public async Task The_grpc_port_speaks_http2_without_tls()
        {
            // Prior-knowledge h2c: exactly what a gRPC client does, and exactly what a mixed
            // cleartext endpoint refuses.
            using var handler = new SocketsHttpHandler();
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{_grpcPort}"),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };

            var response = await client.GetAsync("/say-something");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(HttpVersion.Version20, response.Version);
        }

        [TestMethod]
        public async Task The_rest_port_is_the_one_that_speaks_http1()
        {
            using var client = new HttpClient()
            {
                BaseAddress = new Uri(_restAddress),
                DefaultRequestVersion = HttpVersion.Version11,
            };

            var response = await client.GetAsync("/say-something");

            Assert.AreEqual(HttpVersion.Version11, response.Version);
        }
    }
}
