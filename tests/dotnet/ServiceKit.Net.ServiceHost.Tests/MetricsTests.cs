using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Hosting;

namespace ServiceKit.Net.Tests
{
    // Metrics answer what a trace cannot: not "what happened to this call" but how many, how fast,
    // how often wrong. The host serves its own scrape endpoint, so a service is measurable with
    // nothing installed.
    [TestClass]
    public class MetricsTests
    {
        private static IHost _host;
        private static HttpClient _client;

        [ClassInitialize]
        public static async Task Start(TestContext context)
        {
            // No scrape cache: these tests ask "was that request counted", and a cached answer is one
            // taken before it happened.
            var started = await TestServiceHost.Start(null, $"--{MetricsExtensions.ScrapeCacheConfigurationKey}=0");
            _host = started.Host;
            _client = new HttpClient() { BaseAddress = new Uri(started.Address) };
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

        private static async Task<string> Scrape()
        {
            var response = await _client.GetAsync(MetricsExtensions.DefaultPath);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        [TestMethod]
        public async Task The_host_serves_its_own_scrape_endpoint()
        {
            var response = await _client.GetAsync(MetricsExtensions.DefaultPath);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            // the Prometheus text format, which is what a scraper parses
            StringAssert.Contains(response.Content.Headers.ContentType?.MediaType, "text/plain");
        }

        [TestMethod]
        public async Task Requests_are_counted_and_timed()
        {
            await _client.GetAsync("/say-something");
            await _client.GetAsync("/say-something");

            var scraped = await Scrape();

            StringAssert.Contains(scraped, "http_server_request_duration_seconds");
            StringAssert.Contains(scraped, "/say-something");
        }

        [TestMethod]
        public async Task A_failed_request_is_told_apart_from_a_good_one()
        {
            // The status code is a dimension, not a separate counter: "how often wrong" is a filter
            // on the same series, which is what makes an error rate one query rather than two.
            await _client.GetAsync("/say-something");
            await _client.GetAsync("/blow-up");

            var scraped = await Scrape();

            StringAssert.Contains(scraped, "http_response_status_code=\"200\"");
            StringAssert.Contains(scraped, "http_response_status_code=\"500\"");
        }

        [TestMethod]
        public async Task The_runtime_underneath_is_measured_too()
        {
            // GC and the thread pool are what explain a latency graph; without them a slow service
            // looks the same whether it is starved or just busy.
            var scraped = await Scrape();

            StringAssert.Contains(scraped, "process_runtime_dotnet_gc_collections_count_total");
            StringAssert.Contains(scraped, "process_runtime_dotnet_thread_pool_queue_length");
        }

        [TestMethod]
        public async Task A_counter_a_service_creates_itself_is_exported()
        {
            // ServiceKitDiagnostics.Meter is registered with the provider, so a service counts its
            // own business events without knowing how metrics are configured.
            await _client.GetAsync("/count-something");
            await _client.GetAsync("/count-something");

            var scraped = await Scrape();

            StringAssert.Contains(scraped, "things_counted_total");
        }

        [TestMethod]
        public async Task The_service_and_the_environment_are_on_the_metrics()
        {
            // The same resource the traces carry, so a collector can join the two views of one
            // process.
            var scraped = await Scrape();

            StringAssert.Contains(scraped, "ServiceKit.Net.ServiceHost.Tests");
        }

        [TestMethod]
        public async Task The_scrape_itself_is_not_logged_as_traffic()
        {
            // A scraper arrives every few seconds. At Information it would be all anyone ever sees.
            TestServiceHost.Sink.Clear();

            await Scrape();

            var noisy = TestServiceHost.Sink.Events
                .Where(e => e.RenderMessage().Contains(MetricsExtensions.DefaultPath))
                .Where(e => e.Level >= Serilog.Events.LogEventLevel.Information)
                .ToList();

            Assert.AreEqual(0, noisy.Count, string.Join(" | ", noisy.Select(e => e.RenderMessage())));
        }
    }
}
