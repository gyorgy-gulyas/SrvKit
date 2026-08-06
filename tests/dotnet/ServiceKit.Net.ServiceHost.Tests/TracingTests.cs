using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;

namespace ServiceKit.Net.Tests
{
    // One call through several services has to be ONE thing with one identity. .NET already
    // propagates W3C traceparent; what was missing was a tracer that samples those spans, a source
    // the platform can start its own from, and the tie between a span and the correlation id the
    // logs are searched by.
    [TestClass]
    public class TracingTests
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
        public void ClearWhatWasCollected()
        {
            TestServiceHost.Sink.Clear();
            TestServiceHost.Spans.Clear();
        }

        // The span ends after the response is written, so a test that asks immediately can be early.
        private static void Settle()
        {
            _host.Services.GetRequiredService<TracerProvider>().ForceFlush(2000);
        }

        private static async Task<string> _RawGet(string path, params string[] headers)
        {
            var uri = new Uri(_address);
            using var socket = new System.Net.Sockets.TcpClient();
            await socket.ConnectAsync(uri.Host, uri.Port);

            var request = $"GET {path} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\n"
                + string.Concat(headers.Select(header => header + "\r\n"))
                + "Connection: close\r\n\r\n";

            using var stream = socket.GetStream();
            var bytes = System.Text.Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }

        private static Activity ServerSpanFor(string path)
        {
            return TestServiceHost.Spans
                .Where(span => span.Kind == ActivityKind.Server)
                .FirstOrDefault(span => span.GetTagItem("url.path") as string == path
                                     || span.GetTagItem("http.route") as string == path.TrimStart('/')
                                     || span.DisplayName.Contains(path));
        }

        [TestMethod]
        public async Task A_request_is_traced()
        {
            await _client.GetAsync("/say-something");
            Settle();

            Assert.IsNotNull(ServerSpanFor("/say-something"),
                "spans seen: " + string.Join(" | ", TestServiceHost.Spans.Select(s => $"{s.Kind}:{s.DisplayName}")));
        }

        [TestMethod]
        public async Task The_invented_correlation_id_is_the_trace_id()
        {
            // This is the bridge. A fresh guid would have meant a log line and a trace naming the
            // same call by two unrelated identifiers, and somebody correlating them by hand.
            var response = await _client.GetAsync("/say-something");
            Settle();

            var correlationId = response.Headers.GetValues(ServiceConstans.const_correlation_id).Single();
            var span = ServerSpanFor("/say-something");

            Assert.IsNotNull(span);
            Assert.AreEqual(span.TraceId.ToString(), correlationId);

            // and the same id is on the log line, which is what makes the search work in both
            // directions
            var logged = TestServiceHost.Sink.WithMessageContaining("something happened").Single();
            Assert.AreEqual(correlationId, CapturingSink.PropertyOf(logged, "CorrelationId"));
        }

        [TestMethod]
        public async Task The_call_identity_is_on_the_span_as_well_as_in_the_log()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/say-something");
            request.Headers.Add(ServiceConstans.const_tenant_id, "tenant-1");
            request.Headers.Add(ServiceConstans.const_identity_id, "identity-1");
            request.Headers.Add(ServiceConstans.const_call_stack, "BFF.placeOrder");

            await _client.SendAsync(request);
            Settle();

            var span = ServerSpanFor("/say-something");

            Assert.IsNotNull(span);
            Assert.AreEqual("tenant-1", span.GetTagItem(ServiceKitDiagnostics.tag_tenant_id));
            Assert.AreEqual("identity-1", span.GetTagItem(ServiceKitDiagnostics.tag_identity_id));
            Assert.AreEqual("BFF.placeOrder", span.GetTagItem(ServiceKitDiagnostics.tag_call_stack));
        }

        [TestMethod]
        public async Task A_correlation_id_that_was_sent_is_kept_and_put_on_the_span()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/say-something");
            request.Headers.Add(ServiceConstans.const_correlation_id, "the-caller-s-id");

            var response = await _client.SendAsync(request);
            Settle();

            Assert.AreEqual("the-caller-s-id", response.Headers.GetValues(ServiceConstans.const_correlation_id).Single());
            Assert.AreEqual("the-caller-s-id", ServerSpanFor("/say-something").GetTagItem(ServiceKitDiagnostics.tag_correlation_id));
        }

        [TestMethod]
        public async Task An_incoming_trace_is_continued_rather_than_started_again()
        {
            // The caller's traceparent is what makes two services one trace. Losing it here would
            // leave every service with a trace of its own, which is no trace at all.
            var traceId = ActivityTraceId.CreateRandom().ToHexString();
            var parentSpanId = ActivitySpanId.CreateRandom().ToHexString();

            // Written onto the socket by hand on purpose. The caller here lives in the same process
            // as the host, so an HttpClient would be instrumented too and would overwrite the
            // traceparent with a trace of its own before the request ever left - the test would then
            // be about the test's client, not about the server continuing a trace.
            await _RawGet("/say-something", $"traceparent: 00-{traceId}-{parentSpanId}-01");
            Settle();

            var span = ServerSpanFor("/say-something");

            Assert.IsNotNull(span);
            Assert.AreEqual(traceId, span.TraceId.ToString());
            Assert.AreEqual(parentSpanId, span.ParentSpanId.ToString());
        }

        [TestMethod]
        public async Task A_span_a_service_starts_itself_is_exported()
        {
            // The platform's own ActivitySource is registered with the tracer, so a service that
            // wants a span of its own gets one exported without knowing how tracing is configured.
            await _client.GetAsync("/do-some-work");
            Settle();

            var work = TestServiceHost.Spans.FirstOrDefault(span => span.DisplayName == "some work");

            Assert.IsNotNull(work, "spans seen: " + string.Join(" | ", TestServiceHost.Spans.Select(s => s.DisplayName)));
            Assert.AreEqual("42", work.GetTagItem("work.items")?.ToString());
        }

        [TestMethod]
        public async Task The_health_probes_are_not_traced()
        {
            await _client.GetAsync("/health/live");
            await _client.GetAsync("/health/ready");
            Settle();

            var probes = TestServiceHost.Spans.Where(span => span.DisplayName.Contains("health")).ToList();

            Assert.AreEqual(0, probes.Count, string.Join(" | ", probes.Select(s => s.DisplayName)));
        }
    }
}
