using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ServiceKit.Net.Tests
{
    // A call between two services has to survive the other end restarting. The part worth testing is
    // not that a retry happens - Polly does that - but WHICH calls are allowed to be retried.
    [TestClass]
    public class ResilienceTests
    {
        private sealed class CountingHandler : HttpMessageHandler
        {
            public int Calls;
            public HttpStatusCode Answer = HttpStatusCode.ServiceUnavailable;
            public int SucceedFromCall = int.MaxValue;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;

                var status = Calls >= SucceedFromCall ? HttpStatusCode.OK : Answer;
                return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
            }
        }

        private static (HttpClient Client, CountingHandler Handler) ClientWith(Action<ResilienceExtensions.Options> configure = null)
        {
            var handler = new CountingHandler();

            var services = new ServiceCollection();
            services.AddHttpClient("test")
                .ConfigurePrimaryHttpMessageHandler(() => handler)
                .AddServiceKitResilience(options =>
                {
                    options.RetryDelay = TimeSpan.FromMilliseconds(1);
                    configure?.Invoke(options);
                });

            var provider = services.BuildServiceProvider();
            var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");
            client.BaseAddress = new Uri("http://downstream");

            return (client, handler);
        }

        [TestMethod]
        public async Task A_get_that_the_other_end_could_not_answer_is_tried_again()
        {
            var (client, handler) = ClientWith(options => options.MaximumAttempts = 3);
            handler.SucceedFromCall = 3;

            var response = await client.GetAsync("/orders");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(3, handler.Calls);
        }

        [TestMethod]
        public async Task A_post_is_not_retried()
        {
            // The important default. A retried GET is free; a retried POST can place the order
            // twice, and no amount of convenience makes POST idempotent.
            var (client, handler) = ClientWith(options => options.MaximumAttempts = 3);

            var response = await client.PostAsync("/orders", new StringContent("{}"));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.AreEqual(1, handler.Calls);
        }

        [TestMethod]
        public async Task A_post_is_retried_when_the_caller_says_it_is_safe()
        {
            // For an endpoint that is idempotent in fact - one that takes an idempotency key, or
            // where writing the same thing again is harmless.
            var (client, handler) = ClientWith(options =>
            {
                options.MaximumAttempts = 3;
                options.RetryUnsafeMethods = true;
            });
            handler.SucceedFromCall = 2;

            var response = await client.PostAsync("/orders", new StringContent("{}"));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(2, handler.Calls);
        }

        [TestMethod]
        public async Task An_answer_the_caller_has_to_fix_is_not_tried_again()
        {
            // Repeating a request that was wrong the first time only wastes the other end's
            // capacity.
            var (client, handler) = ClientWith(options => options.MaximumAttempts = 3);
            handler.Answer = HttpStatusCode.BadRequest;

            var response = await client.GetAsync("/orders");

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual(1, handler.Calls);
        }

        [TestMethod]
        public async Task One_attempt_means_one_attempt()
        {
            var (client, handler) = ClientWith(options => options.MaximumAttempts = 1);

            await client.GetAsync("/orders");

            Assert.AreEqual(1, handler.Calls);
        }

        [TestMethod]
        public void The_grpc_channel_retries_only_what_was_never_carried_out()
        {
            // Unavailable is "nobody answered", DeadlineExceeded is "not in time", ResourceExhausted
            // is "not now". None of them says the call ran.
            var options = ResilienceExtensions.ServiceKitChannelOptions(o => o.MaximumAttempts = 4);

            var policy = options.ServiceConfig.MethodConfigs.Single().RetryPolicy;

            Assert.AreEqual(4, policy.MaxAttempts);
            CollectionAssert.AreEquivalent(
                new[] { Grpc.Core.StatusCode.Unavailable, Grpc.Core.StatusCode.DeadlineExceeded, Grpc.Core.StatusCode.ResourceExhausted },
                policy.RetryableStatusCodes.ToArray());
        }

        [TestMethod]
        public void A_grpc_call_gets_a_deadline_by_default()
        {
            // A call without one waits forever, which is how a single stuck dependency takes a whole
            // service with it.
            var deadline = ResilienceExtensions.DefaultDeadline(o => o.TotalTimeout = TimeSpan.FromSeconds(30));

            Assert.IsTrue(deadline > DateTime.UtcNow);
            Assert.IsTrue(deadline <= DateTime.UtcNow.AddSeconds(31));
        }
    }
}
