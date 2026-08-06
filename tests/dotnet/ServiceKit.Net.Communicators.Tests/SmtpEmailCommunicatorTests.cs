using Microsoft.Extensions.Configuration;
using MimeKit;
using ServiceKit.Net.Communicators.Implementations;

namespace ServiceKit.Net.Communicators.Tests
{
    // The e-mail channel used to answer Success() without calling anything. These tests exist so
    // that "the message was sent" means a message actually left, and says what it was asked to say.
    [TestClass]
    public class SmtpEmailCommunicatorTests
    {
        private TestSmtpServer _server;

        [TestInitialize]
        public void Setup()
        {
            _server = new TestSmtpServer();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _server?.Dispose();
        }

        private IEmailCommunicator Configured()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>()
                {
                    ["Smtp:Host"] = "127.0.0.1",
                    ["Smtp:Port"] = _server.Port.ToString(),
                    ["Smtp:From"] = "noreply@example.com",
                    ["Smtp:FromDisplayName"] = "MicronIQ",
                    // no TLS to a listener in the same process
                    ["Smtp:Security"] = "None",
                })
                .Build();

            return new SmtpEmailCommunicator(configuration, null);
        }

        private static IEmailCommunicator Unconfigured()
        {
            return new SmtpEmailCommunicator(new ConfigurationBuilder().Build(), null);
        }

        [TestMethod]
        public async Task An_unconfigured_channel_fails_instead_of_swallowing_the_message()
        {
            // The stub this replaces returned Success() and dropped the mail on the floor, which is
            // the worst of both worlds: nothing sent and nobody told.
            var answer = await Unconfigured().SendEmail("subject", "body", new[] { "someone@example.com" });

            Assert.IsTrue(answer.IsFailed());
            Assert.AreEqual(Statuses.InternalError, answer.Status);
            StringAssert.Contains(answer.Errors[0].AdditionalInformation, "Smtp:Host");
        }

        [TestMethod]
        public async Task A_message_with_no_recipient_is_the_callers_mistake()
        {
            var answer = await Configured().SendEmail(new IEmailCommunicator.Message() { Subject = "nobody" });

            Assert.AreEqual(Statuses.BadRequest, answer.Status);
            Assert.AreEqual(0, _server.Messages.Count);
        }

        [TestMethod]
        public async Task The_ordinary_case_arrives_as_it_was_written()
        {
            var answer = await Configured().SendEmail("Order placed", "Your order is on its way.", new[] { "buyer@example.com" });

            Assert.IsTrue(answer.IsSuccess(), string.Join(" | ", answer.Errors.Select(e => e.MessageText)));

            var received = _server.Messages.Single();
            Assert.AreEqual("Order placed", received.Message.Subject);
            Assert.AreEqual("buyer@example.com", received.Message.To.Mailboxes.Single().Address);
            Assert.AreEqual("noreply@example.com", received.Message.From.Mailboxes.Single().Address);
            Assert.AreEqual("MicronIQ", received.Message.From.Mailboxes.Single().Name);
            StringAssert.Contains(received.Message.TextBody, "on its way");
        }

        [TestMethod]
        public async Task A_copy_is_visible_and_a_blind_copy_is_not()
        {
            // The whole point of the two fields. A Bcc that shows up in the headers is not a Bcc,
            // and one that never reaches the envelope is not a copy at all.
            var answer = await Configured().SendEmail(new IEmailCommunicator.Message()
            {
                Subject = "Order placed",
                Body = "text",
                To = new[] { "buyer@example.com" },
                Cc = new[] { "sales@example.com" },
                Bcc = new[] { "audit@example.com" },
            });

            Assert.IsTrue(answer.IsSuccess());

            var received = _server.Messages.Single();

            Assert.AreEqual("sales@example.com", received.Message.Cc.Mailboxes.Single().Address);
            Assert.AreEqual(0, received.Message.Bcc.Mailboxes.Count(), "the blind copy must not be in the headers");

            // ...but it is delivered, which only the envelope can show
            CollectionAssert.AreEquivalent(
                new[] { "buyer@example.com", "sales@example.com", "audit@example.com" },
                received.EnvelopeRecipients);
        }

        [TestMethod]
        public async Task An_html_body_is_sent_as_markup()
        {
            var answer = await Configured().SendEmail(new IEmailCommunicator.Message()
            {
                Subject = "One time password",
                Body = "<p>Your code is <b>123456</b></p>",
                BodyFormat = IEmailCommunicator.BodyFormats.Html,
                To = new[] { "buyer@example.com" },
            });

            Assert.IsTrue(answer.IsSuccess());

            var received = _server.Messages.Single();
            Assert.IsNotNull(received.Message.HtmlBody);
            StringAssert.Contains(received.Message.HtmlBody, "<b>123456</b>");
        }

        [TestMethod]
        public async Task An_attachment_arrives_with_its_name_and_type()
        {
            var answer = await Configured().SendEmail(new IEmailCommunicator.Message()
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

            Assert.IsTrue(answer.IsSuccess());

            var attachment = (MimePart)_server.Messages.Single().Message.Attachments.Single();
            Assert.AreEqual("invoice.pdf", attachment.FileName);
            Assert.AreEqual("application/pdf", attachment.ContentType.MimeType);
        }

        [TestMethod]
        public async Task An_attachment_with_a_content_id_is_part_of_the_message_and_not_a_download()
        {
            // That is what lets an HTML body show a logo: <img src="cid:logo">. The same bytes
            // offered as a file would be a second thing to click instead of a picture in the mail.
            var answer = await Configured().SendEmail(new IEmailCommunicator.Message()
            {
                Subject = "Welcome",
                Body = "<p><img src=\"cid:logo\"></p>",
                BodyFormat = IEmailCommunicator.BodyFormats.Html,
                To = new[] { "buyer@example.com" },
                Attachments = new[]
                {
                    new IEmailCommunicator.Attachment()
                    {
                        Content = new byte[] { 9, 9, 9 },
                        ContentType = "image/png",
                        FileName = "logo.png",
                        ContentId = "logo",
                    },
                },
            });

            Assert.IsTrue(answer.IsSuccess());

            var message = _server.Messages.Single().Message;
            Assert.AreEqual(0, message.Attachments.Count(), "an inline part is not an attachment");

            var inline = message.BodyParts.OfType<MimePart>().Single(part => part.ContentId == "logo");
            Assert.AreEqual("image/png", inline.ContentType.MimeType);
        }

        [TestMethod]
        public async Task A_reply_goes_where_the_message_says_and_not_to_the_sender()
        {
            var answer = await Configured().SendEmail(new IEmailCommunicator.Message()
            {
                Subject = "Support",
                Body = "text",
                To = new[] { "buyer@example.com" },
                ReplyTo = "support@example.com",
            });

            Assert.IsTrue(answer.IsSuccess());
            Assert.AreEqual("support@example.com", _server.Messages.Single().Message.ReplyTo.Mailboxes.Single().Address);
        }

        [TestMethod]
        public async Task The_message_may_override_the_configured_sender()
        {
            var answer = await Configured().SendEmail(new IEmailCommunicator.Message()
            {
                Subject = "From the shop",
                Body = "text",
                From = "shop@example.com",
                FromDisplayName = "The Shop",
                To = new[] { "buyer@example.com" },
            });

            Assert.IsTrue(answer.IsSuccess());

            var from = _server.Messages.Single().Message.From.Mailboxes.Single();
            Assert.AreEqual("shop@example.com", from.Address);
            Assert.AreEqual("The Shop", from.Name);
        }

        [TestMethod]
        public async Task A_server_that_is_not_there_is_reported_rather_than_thrown()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>()
                {
                    // nothing listens here
                    ["Smtp:Host"] = "127.0.0.1",
                    ["Smtp:Port"] = "1",
                    ["Smtp:From"] = "noreply@example.com",
                    ["Smtp:Security"] = "None",
                })
                .Build();

            var answer = await ((IEmailCommunicator)new SmtpEmailCommunicator(configuration, null)).SendEmail(
                new IEmailCommunicator.Message() { Subject = "s", Body = "b", To = new[] { "someone@example.com" } });

            Assert.IsTrue(answer.IsFailed());
            Assert.AreEqual(Statuses.InternalError, answer.Status);
        }
    }
}
