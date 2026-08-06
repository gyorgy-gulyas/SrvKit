using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ServiceKit.Net.Communicators.Implementations;

namespace ServiceKit.Net.Communicators.Tests
{
    // The channels the platform vision listed and only e-mail and SMS existed for.
    [TestClass]
    public class WebhookTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            public HttpRequestMessage Request;
            public string RequestBody;
            public HttpStatusCode Status = HttpStatusCode.OK;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Request = request;
                RequestBody = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
                return new HttpResponseMessage(Status);
            }
        }

        private sealed class StubClientFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;

            public StubClientFactory(HttpMessageHandler handler)
            {
                _handler = handler;
            }

            public HttpClient CreateClient(string name)
            {
                return new HttpClient(_handler, disposeHandler: false);
            }
        }

        private StubHandler _handler;

        [TestInitialize]
        public void Setup()
        {
            _handler = new StubHandler();
        }

        private IWebhookCommunicator Configured(string secret = "a-shared-secret")
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>() { ["Webhook:SigningSecret"] = secret })
                .Build();

            return new HttpWebhookCommunicator(configuration, null, new StubClientFactory(_handler));
        }

        private static IWebhookCommunicator.Delivery ADelivery()
        {
            return new IWebhookCommunicator.Delivery()
            {
                Url = "https://subscriber.example.com/hooks",
                EventType = "order.placed",
                Payload = new { orderId = "order-1", total = 1234 },
            };
        }

        [TestMethod]
        public async Task An_unsigned_channel_refuses_to_send()
        {
            IWebhookCommunicator communicator = new HttpWebhookCommunicator(new ConfigurationBuilder().Build(), null, new StubClientFactory(_handler));

            var answer = await communicator.Send(ADelivery());

            Assert.IsTrue(answer.IsFailed());
            Assert.IsNull(_handler.Request, "an unsigned webhook is one the receiver cannot believe, so it is not sent at all");
        }

        [TestMethod]
        public async Task A_delivery_with_no_address_is_the_callers_mistake()
        {
            var answer = await Configured().Send(new IWebhookCommunicator.Delivery() { EventType = "order.placed" });

            Assert.AreEqual(Statuses.BadRequest, answer.Status);
        }

        [TestMethod]
        public async Task The_receiver_can_verify_that_the_call_came_from_us()
        {
            // The whole point of the signature: recomputed from the timestamp and the exact body,
            // with a secret only the two ends know.
            var answer = await Configured().Send(ADelivery());

            Assert.IsTrue(answer.IsSuccess());

            var timestamp = _handler.Request.Headers.GetValues(HttpWebhookCommunicator.TimestampHeader).Single();
            var signature = _handler.Request.Headers.GetValues(HttpWebhookCommunicator.SignatureHeader).Single();

            Assert.AreEqual(HttpWebhookCommunicator.Sign("a-shared-secret", timestamp, _handler.RequestBody), signature);
        }

        [TestMethod]
        public async Task A_body_that_was_tampered_with_no_longer_matches()
        {
            await Configured().Send(ADelivery());

            var timestamp = _handler.Request.Headers.GetValues(HttpWebhookCommunicator.TimestampHeader).Single();
            var signature = _handler.Request.Headers.GetValues(HttpWebhookCommunicator.SignatureHeader).Single();

            Assert.AreNotEqual(HttpWebhookCommunicator.Sign("a-shared-secret", timestamp, _handler.RequestBody + " "), signature);
        }

        [TestMethod]
        public async Task The_timestamp_is_inside_the_signature_so_a_capture_cannot_be_replayed_forever()
        {
            await Configured().Send(ADelivery());

            var signature = _handler.Request.Headers.GetValues(HttpWebhookCommunicator.SignatureHeader).Single();

            Assert.AreNotEqual(HttpWebhookCommunicator.Sign("a-shared-secret", "0", _handler.RequestBody), signature);
        }

        [TestMethod]
        public async Task Every_delivery_can_be_recognised_and_dropped_if_it_arrives_twice()
        {
            // This is what makes retrying a webhook POST safe, and why the retry is turned on for
            // this channel and off everywhere else.
            await Configured().Send(ADelivery());

            var deliveryId = _handler.Request.Headers.GetValues(HttpWebhookCommunicator.DeliveryIdHeader).Single();

            Assert.IsFalse(string.IsNullOrWhiteSpace(deliveryId));
            Assert.AreEqual("order.placed", _handler.Request.Headers.GetValues(HttpWebhookCommunicator.EventTypeHeader).Single());
        }

        [TestMethod]
        public async Task The_caller_may_name_the_delivery_itself()
        {
            // So a redelivery of the SAME event carries the same id and the receiver still knows.
            var delivery = ADelivery();
            delivery.DeliveryId = "delivery-1";

            await Configured().Send(delivery);

            Assert.AreEqual("delivery-1", _handler.Request.Headers.GetValues(HttpWebhookCommunicator.DeliveryIdHeader).Single());
        }

        [TestMethod]
        public async Task The_payload_travels_as_json()
        {
            await Configured().Send(ADelivery());

            using var document = JsonDocument.Parse(_handler.RequestBody);

            Assert.AreEqual("order-1", document.RootElement.GetProperty("orderId").GetString());
            Assert.AreEqual("application/json", _handler.Request.Content.Headers.ContentType.MediaType);
        }

        [TestMethod]
        public async Task A_receiver_that_refuses_is_reported_with_its_own_answer()
        {
            _handler.Status = HttpStatusCode.NotFound;

            var answer = await Configured().Send(ADelivery());

            Assert.IsTrue(answer.IsFailed());
            Assert.AreEqual(Statuses.NotFound, answer.Status);
        }
    }

    [TestClass]
    public class PushTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            public HttpRequestMessage Request;
            public string RequestBody;
            public HttpStatusCode Status = HttpStatusCode.OK;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Request = request;
                RequestBody = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
                return new HttpResponseMessage(Status) { Content = new StringContent("") };
            }
        }

        private sealed class StubClientFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;

            public StubClientFactory(HttpMessageHandler handler)
            {
                _handler = handler;
            }

            public HttpClient CreateClient(string name)
            {
                return new HttpClient(_handler, disposeHandler: false);
            }
        }

        private StubHandler _handler;

        [TestInitialize]
        public void Setup()
        {
            _handler = new StubHandler();
        }

        private IPushCommunicator Configured()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>()
                {
                    ["Push:Endpoint"] = "https://push.example.com/send",
                    ["Push:ApiKey"] = "a-key",
                })
                .Build();

            return new HttpPushCommunicator(configuration, null, new StubClientFactory(_handler));
        }

        [TestMethod]
        public async Task An_unconfigured_channel_fails_instead_of_swallowing_the_notification()
        {
            IPushCommunicator communicator = new HttpPushCommunicator(new ConfigurationBuilder().Build(), null, new StubClientFactory(_handler));

            var answer = await communicator.Send(new IPushCommunicator.Notification() { DeviceTokens = new[] { "device-1" } });

            Assert.IsTrue(answer.IsFailed());
            Assert.IsNull(_handler.Request);
        }

        [TestMethod]
        public async Task A_notification_with_no_device_is_the_callers_mistake()
        {
            var answer = await Configured().Send(new IPushCommunicator.Notification() { Title = "nobody" });

            Assert.AreEqual(Statuses.BadRequest, answer.Status);
        }

        [TestMethod]
        public async Task What_is_shown_and_what_is_carried_are_kept_apart()
        {
            // The data is what lets a tap open the order it was about instead of the home screen.
            var answer = await Configured().Send(new IPushCommunicator.Notification()
            {
                DeviceTokens = new[] { "device-1", "device-2" },
                Title = "Order shipped",
                Body = "Your order is on its way",
                Data = new Dictionary<string, string>() { ["orderId"] = "order-1" },
            });

            Assert.IsTrue(answer.IsSuccess());

            using var document = JsonDocument.Parse(_handler.RequestBody);
            Assert.AreEqual(2, document.RootElement.GetProperty("tokens").GetArrayLength());
            Assert.AreEqual("Order shipped", document.RootElement.GetProperty("title").GetString());
            Assert.AreEqual("order-1", document.RootElement.GetProperty("data").GetProperty("orderId").GetString());
            Assert.AreEqual("a-key", _handler.Request.Headers.Authorization.Parameter);
        }
    }

    [TestClass]
    public class InnerNotificationTests
    {
        [TestMethod]
        public async Task What_was_sent_is_what_is_unread()
        {
            IInnerNotificationCommunicator communicator = new InMemoryInnerNotificationCommunicator();

            await communicator.Notify(new IInnerNotificationCommunicator.Notification()
            {
                RecipientIdentityId = "user-1",
                Title = "Order shipped",
                Link = "/orders/order-1",
            });

            var unread = await communicator.Unread("user-1");

            Assert.IsTrue(unread.IsSuccess());
            Assert.AreEqual("Order shipped", unread.Value.Single().Title);
        }

        [TestMethod]
        public async Task One_recipients_bell_is_not_another_s()
        {
            IInnerNotificationCommunicator communicator = new InMemoryInnerNotificationCommunicator();
            await communicator.Notify(new IInnerNotificationCommunicator.Notification() { RecipientIdentityId = "user-1", Title = "a" });

            var unread = await communicator.Unread("user-2");

            Assert.AreEqual(0, unread.Value.Count);
        }

        [TestMethod]
        public async Task Reading_them_clears_them()
        {
            IInnerNotificationCommunicator communicator = new InMemoryInnerNotificationCommunicator();
            await communicator.Notify(new IInnerNotificationCommunicator.Notification() { RecipientIdentityId = "user-1", Title = "a" });

            await communicator.MarkAllRead("user-1");

            Assert.AreEqual(0, (await communicator.Unread("user-1")).Value.Count);
        }

        [TestMethod]
        public async Task A_notification_with_no_recipient_is_refused()
        {
            IInnerNotificationCommunicator communicator = new InMemoryInnerNotificationCommunicator();

            var answer = await communicator.Notify(new IInnerNotificationCommunicator.Notification() { Title = "a" });

            Assert.AreEqual(Statuses.BadRequest, answer.Status);
        }
    }
}
