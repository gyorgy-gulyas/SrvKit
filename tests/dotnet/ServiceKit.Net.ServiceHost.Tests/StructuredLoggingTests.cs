using System.Net.Http;
using Microsoft.Extensions.Hosting;

namespace ServiceKit.Net.Tests
{
    // What a log line has to carry before it is worth writing: which call it belongs to, whose call
    // it was, and for which tenant. None of it was there - the controllers pushed their scope onto a
    // Serilog that the host had never configured.
    [TestClass]
    public class StructuredLoggingTests
    {
        private static IHost _host;
        private static string _address;
        private static HttpClient _client;

        [ClassInitialize]
        public static async Task Start(TestContext context)
        {
            var started = await TestServiceHost.Start();
            _host = started.Host;
            _address = started.Address;
            _client = new HttpClient() { BaseAddress = new Uri(_address) };
        }

        [ClassCleanup]
        public static async Task Stop()
        {
            _client?.Dispose();
            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
        }

        [TestInitialize]
        public void ClearTheSink()
        {
            TestServiceHost.Sink.Clear();
        }

        private static HttpRequestMessage ARequest(params (string Name, string Value)[] headers)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/say-something");
            foreach (var header in headers)
                request.Headers.Add(header.Name, header.Value);

            return request;
        }

        [TestMethod]
        public async Task A_request_without_a_correlation_id_is_given_one()
        {
            // The entry point of a chain is called by a browser, which sends no correlation id. That
            // is precisely the request everything else descends from, so leaving it empty made the
            // whole idea useless.
            var response = await _client.SendAsync(ARequest());

            Assert.IsTrue(response.Headers.TryGetValues(ServiceConstans.const_correlation_id, out var answered));
            var correlationId = answered.Single();
            Assert.IsFalse(string.IsNullOrWhiteSpace(correlationId));
            Assert.IsTrue(Guid.TryParse(correlationId, out _));

            var logged = TestServiceHost.Sink.WithMessageContaining("something happened").Single();
            Assert.AreEqual(correlationId, CapturingSink.PropertyOf(logged, "CorrelationId"));
        }

        [TestMethod]
        public async Task A_correlation_id_that_was_sent_is_the_one_that_is_used()
        {
            var response = await _client.SendAsync(ARequest((ServiceConstans.const_correlation_id, "the-caller-s-id")));

            Assert.AreEqual("the-caller-s-id", response.Headers.GetValues(ServiceConstans.const_correlation_id).Single());

            var logged = TestServiceHost.Sink.WithMessageContaining("something happened").Single();
            Assert.AreEqual("the-caller-s-id", CapturingSink.PropertyOf(logged, "CorrelationId"));
        }

        [TestMethod]
        public async Task The_tenant_the_identity_and_the_call_stack_travel_onto_every_line()
        {
            await _client.SendAsync(ARequest(
                (ServiceConstans.const_tenant_id, "tenant-1"),
                (ServiceConstans.const_identity_id, "identity-1"),
                (ServiceConstans.const_call_stack, "BFF.placeOrder"),
                (ServiceConstans.const_client_application, "webshop-bff")));

            var logged = TestServiceHost.Sink.WithMessageContaining("something happened").Single();

            Assert.AreEqual("tenant-1", CapturingSink.PropertyOf(logged, "TenantId"));
            Assert.AreEqual("identity-1", CapturingSink.PropertyOf(logged, "IdentityId"));
            Assert.AreEqual("BFF.placeOrder", CapturingSink.PropertyOf(logged, "CallStack"));
            Assert.AreEqual("webshop-bff", CapturingSink.PropertyOf(logged, "ClientApplication"));
        }

        [TestMethod]
        public async Task What_was_not_sent_is_absent_rather_than_empty()
        {
            // An empty property fills every line with noise and makes a missing tenant look like a
            // tenant named "".
            await _client.SendAsync(ARequest());

            var logged = TestServiceHost.Sink.WithMessageContaining("something happened").Single();

            Assert.IsFalse(logged.Properties.ContainsKey("TenantId"));
            Assert.IsFalse(logged.Properties.ContainsKey("IdentityId"));
            Assert.IsFalse(logged.Properties.ContainsKey("CallStack"));
        }

        [TestMethod]
        public async Task Every_line_says_which_service_and_environment_it_came_from()
        {
            await _client.SendAsync(ARequest());

            var logged = TestServiceHost.Sink.WithMessageContaining("something happened").Single();

            Assert.AreEqual("ServiceKit.Net.ServiceHost.Tests", CapturingSink.PropertyOf(logged, "Service"));
            Assert.IsFalse(string.IsNullOrWhiteSpace(CapturingSink.PropertyOf(logged, "Environment")));
        }

        [TestMethod]
        public async Task The_correlation_id_reaches_the_calling_context_the_controllers_build()
        {
            // The middleware writes the id back into the REQUEST headers, which is what makes it
            // travel on to the next service instead of stopping at this one.
            var request = new HttpRequestMessage(HttpMethod.Get, "/what-do-you-see");
            var response = await _client.SendAsync(request);
            var seen = await response.Content.ReadAsStringAsync();

            var answered = response.Headers.GetValues(ServiceConstans.const_correlation_id).Single();
            Assert.AreEqual(answered, seen);
        }

        [TestMethod]
        public async Task One_summary_line_is_written_per_request()
        {
            await _client.SendAsync(ARequest());

            var summaries = TestServiceHost.Sink.Events
                .Where(e => e.MessageTemplate.Text.Contains("responded"))
                .Where(e => CapturingSink.PropertyOf(e, "RequestPath") == "/say-something")
                .ToList();

            Assert.AreEqual(1, summaries.Count);
            Assert.IsTrue(summaries[0].Properties.ContainsKey("CorrelationId"));
            Assert.AreEqual("200", CapturingSink.PropertyOf(summaries[0], "StatusCode"));
        }

        [TestMethod]
        public async Task The_health_probes_do_not_drown_out_the_log()
        {
            // The orchestrator calls these every few seconds; at Information they would be all
            // anyone ever sees.
            await _client.GetAsync("/health/live");
            await _client.GetAsync("/health/ready");

            var noisy = TestServiceHost.Sink.Events
                .Where(e => e.RenderMessage().Contains("/health/"))
                .Where(e => e.Level >= Serilog.Events.LogEventLevel.Information)
                .ToList();

            Assert.AreEqual(0, noisy.Count, string.Join(" | ", noisy.Select(e => e.RenderMessage())));
        }
    }
}
