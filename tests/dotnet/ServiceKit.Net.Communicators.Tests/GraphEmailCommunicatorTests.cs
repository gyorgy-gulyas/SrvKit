using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ServiceKit.Net.Communicators.Implementations;

namespace ServiceKit.Net.Communicators.Tests
{
    // Graph is tested at the HTTP boundary: the request it builds and what it does with the answer.
    // A tenant to get a real token from is not something a test should need, and the token is the
    // one part of this that is not ours to check.
    [TestClass]
    public class GraphEmailCommunicatorTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            public HttpRequestMessage Request;
            public string RequestBody;
            public HttpStatusCode Status = HttpStatusCode.Accepted;
            public string ResponseBody = string.Empty;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Request = request;
                RequestBody = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;

                return new HttpResponseMessage(Status) { Content = new StringContent(ResponseBody) };
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

        private sealed class TestableGraphCommunicator : GraphEmailCommunicator
        {
            public TestableGraphCommunicator(IConfiguration configuration, IHttpClientFactory factory)
                : base(configuration, null, factory)
            {
            }

            protected override Task<string> AcquireToken(CancellationToken cancellationToken)
            {
                return Task.FromResult("a-test-token");
            }
        }

        private StubHandler _handler;

        [TestInitialize]
        public void Setup()
        {
            _handler = new StubHandler();
        }

        private IEmailCommunicator Configured()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>()
                {
                    ["Graph:TenantId"] = "a-tenant",
                    ["Graph:ClientId"] = "a-client",
                    ["Graph:ClientSecret"] = "a-secret",
                    ["Graph:From"] = "noreply@example.com",
                })
                .Build();

            return new TestableGraphCommunicator(configuration, new StubClientFactory(_handler));
        }

        private JsonElement SentMessage()
        {
            using var document = JsonDocument.Parse(_handler.RequestBody);
            return document.RootElement.GetProperty("message").Clone();
        }

        private static string[] AddressesOf(JsonElement message, string field)
        {
            if (message.TryGetProperty(field, out var recipients) == false)
                return Array.Empty<string>();

            return recipients.EnumerateArray()
                .Select(recipient => recipient.GetProperty("emailAddress").GetProperty("address").GetString())
                .ToArray();
        }

        [TestMethod]
        public async Task An_unconfigured_channel_fails_instead_of_swallowing_the_message()
        {
            var communicator = new TestableGraphCommunicator(new ConfigurationBuilder().Build(), new StubClientFactory(_handler));

            var answer = await ((IEmailCommunicator)communicator).SendEmail("subject", "body", new[] { "someone@example.com" });

            Assert.IsTrue(answer.IsFailed());
            Assert.AreEqual(Statuses.InternalError, answer.Status);
            StringAssert.Contains(answer.Errors[0].AdditionalInformation, "Graph:TenantId");
            Assert.IsNull(_handler.Request, "nothing should have been sent");
        }

        [TestMethod]
        public async Task A_message_with_no_recipient_is_the_callers_mistake()
        {
            var answer = await Configured().SendEmail(new IEmailCommunicator.Message() { Subject = "nobody" });

            Assert.AreEqual(Statuses.BadRequest, answer.Status);
            Assert.IsNull(_handler.Request);
        }

        [TestMethod]
        public async Task The_message_is_posted_to_the_senders_mailbox_with_a_bearer_token()
        {
            var answer = await Configured().SendEmail("Order placed", "text", new[] { "buyer@example.com" });

            Assert.IsTrue(answer.IsSuccess(), string.Join(" | ", answer.Errors.Select(e => e.MessageText)));
            Assert.AreEqual(HttpMethod.Post, _handler.Request.Method);
            StringAssert.EndsWith(_handler.Request.RequestUri.AbsolutePath, "/users/noreply%40example.com/sendMail");
            Assert.AreEqual("Bearer", _handler.Request.Headers.Authorization.Scheme);
            Assert.AreEqual("a-test-token", _handler.Request.Headers.Authorization.Parameter);
        }

        [TestMethod]
        public async Task The_subject_and_the_body_are_what_was_asked_for()
        {
            await Configured().SendEmail("Order placed", "Your order is on its way.", new[] { "buyer@example.com" });

            var message = SentMessage();
            Assert.AreEqual("Order placed", message.GetProperty("subject").GetString());
            Assert.AreEqual("Text", message.GetProperty("body").GetProperty("contentType").GetString());
            Assert.AreEqual("Your order is on its way.", message.GetProperty("body").GetProperty("content").GetString());
        }

        [TestMethod]
        public async Task An_html_body_says_so()
        {
            await Configured().SendEmail(new IEmailCommunicator.Message()
            {
                Subject = "One time password",
                Body = "<b>123456</b>",
                BodyFormat = IEmailCommunicator.BodyFormats.Html,
                To = new[] { "buyer@example.com" },
            });

            Assert.AreEqual("HTML", SentMessage().GetProperty("body").GetProperty("contentType").GetString());
        }

        [TestMethod]
        public async Task The_three_recipient_fields_are_kept_apart()
        {
            await Configured().SendEmail(new IEmailCommunicator.Message()
            {
                Subject = "Order placed",
                Body = "text",
                To = new[] { "buyer@example.com" },
                Cc = new[] { "sales@example.com" },
                Bcc = new[] { "audit@example.com" },
                ReplyTo = "support@example.com",
            });

            var message = SentMessage();
            CollectionAssert.AreEqual(new[] { "buyer@example.com" }, AddressesOf(message, "toRecipients"));
            CollectionAssert.AreEqual(new[] { "sales@example.com" }, AddressesOf(message, "ccRecipients"));
            CollectionAssert.AreEqual(new[] { "audit@example.com" }, AddressesOf(message, "bccRecipients"));
            CollectionAssert.AreEqual(new[] { "support@example.com" }, AddressesOf(message, "replyTo"));
        }

        [TestMethod]
        public async Task An_attachment_travels_as_bytes_with_its_name_and_type()
        {
            await Configured().SendEmail(new IEmailCommunicator.Message()
            {
                Subject = "Invoice",
                Body = "attached",
                To = new[] { "buyer@example.com" },
                Attachments = new[]
                {
                    new IEmailCommunicator.Attachment()
                    {
                        Content = new byte[] { 1, 2, 3, 4 },
                        ContentType = "application/pdf",
                        FileName = "invoice.pdf",
                    },
                },
            });

            var attachment = SentMessage().GetProperty("attachments").EnumerateArray().Single();
            Assert.AreEqual("#microsoft.graph.fileAttachment", attachment.GetProperty("@odata.type").GetString());
            Assert.AreEqual("invoice.pdf", attachment.GetProperty("name").GetString());
            Assert.AreEqual("application/pdf", attachment.GetProperty("contentType").GetString());
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, Convert.FromBase64String(attachment.GetProperty("contentBytes").GetString()));
            Assert.IsFalse(attachment.TryGetProperty("isInline", out _), "a plain attachment is not inline");
        }

        [TestMethod]
        public async Task An_attachment_with_a_content_id_is_marked_inline()
        {
            await Configured().SendEmail(new IEmailCommunicator.Message()
            {
                Subject = "Welcome",
                Body = "<img src=\"cid:logo\">",
                BodyFormat = IEmailCommunicator.BodyFormats.Html,
                To = new[] { "buyer@example.com" },
                Attachments = new[]
                {
                    new IEmailCommunicator.Attachment()
                    {
                        Content = new byte[] { 9 },
                        ContentType = "image/png",
                        FileName = "logo.png",
                        ContentId = "logo",
                    },
                },
            });

            var attachment = SentMessage().GetProperty("attachments").EnumerateArray().Single();
            Assert.AreEqual("logo", attachment.GetProperty("contentId").GetString());
            Assert.IsTrue(attachment.GetProperty("isInline").GetBoolean());
        }

        [TestMethod]
        public async Task The_sent_items_folder_is_left_alone_unless_asked()
        {
            // A service account's Sent Items is a copy of every notification the system has ever
            // sent, and nobody reads it.
            await Configured().SendEmail("subject", "body", new[] { "buyer@example.com" });

            using var document = JsonDocument.Parse(_handler.RequestBody);
            Assert.IsFalse(document.RootElement.GetProperty("saveToSentItems").GetBoolean());
        }

        [TestMethod]
        public async Task A_refusal_from_graph_keeps_its_status_and_its_explanation()
        {
            // "the mailbox does not exist" and "the application has no permission" are different
            // problems and used to look identical from here.
            _handler.Status = HttpStatusCode.Forbidden;
            _handler.ResponseBody = "{\"error\":{\"code\":\"ErrorAccessDenied\"}}";

            var answer = await Configured().SendEmail("subject", "body", new[] { "buyer@example.com" });

            Assert.IsTrue(answer.IsFailed());
            Assert.AreEqual(Statuses.Forbidden, answer.Status);
            StringAssert.Contains(answer.Errors[0].AdditionalInformation, "ErrorAccessDenied");
        }

        [TestMethod]
        public async Task The_message_may_override_the_configured_sender()
        {
            await Configured().SendEmail(new IEmailCommunicator.Message()
            {
                Subject = "From the shop",
                Body = "text",
                From = "shop@example.com",
                To = new[] { "buyer@example.com" },
            });

            StringAssert.EndsWith(_handler.Request.RequestUri.AbsolutePath, "/users/shop%40example.com/sendMail");
        }
    }
}
