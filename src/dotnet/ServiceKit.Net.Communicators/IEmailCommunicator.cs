namespace ServiceKit.Net.Communicators
{
    /// <summary>
    /// One way out for e-mail, whatever carries it.
    ///
    /// The message is an object rather than a parameter list on purpose: a signature grows a
    /// parameter every time somebody needs a copy recipient or an HTML body, and every growth
    /// breaks every caller. This one grows a property.
    /// </summary>
    public interface IEmailCommunicator
    {
        public class Attachment
        {
            public byte[] Content;
            public string ContentType;
            public string FileName;

            /// <summary>
            /// Set this to reference the attachment from an HTML body as &lt;img src="cid:the-id"&gt;.
            /// An attachment WITH a content id is part of the message and is not offered as a
            /// download; one without it is a file the reader can save.
            /// </summary>
            public string ContentId;
        }

        public enum BodyFormats
        {
            /// The body is written as it will be read. Safe with anything.
            PlainText,

            /// The body is markup. Anything inserted into it has to be escaped by whoever built it -
            /// an OTP wrapped in angle brackets renders as an unknown tag and disappears.
            Html,
        }

        public class Message
        {
            /// <summary>Who it is from. Left empty, the configured sender of the channel is used.</summary>
            public string From;

            /// <summary>The name shown instead of the bare address.</summary>
            public string FromDisplayName;

            /// <summary>Where a reply should go, when that is not the sender.</summary>
            public string ReplyTo;

            public IEnumerable<string> To;

            /// <summary>Copy. Every recipient sees these addresses.</summary>
            public IEnumerable<string> Cc;

            /// <summary>Blind copy. These addresses are on no recipient's copy of the message.</summary>
            public IEnumerable<string> Bcc;

            public string Subject;

            public string Body;

            public BodyFormats BodyFormat = BodyFormats.PlainText;

            public IEnumerable<Attachment> Attachments;

            /// <summary>Everyone who will receive this, whichever field they were named in.</summary>
            public IEnumerable<string> AllRecipients()
            {
                return (To ?? Enumerable.Empty<string>())
                    .Concat(Cc ?? Enumerable.Empty<string>())
                    .Concat(Bcc ?? Enumerable.Empty<string>())
                    .Where(address => string.IsNullOrWhiteSpace(address) == false);
            }

            public bool HasRecipient()
            {
                return AllRecipients().Any();
            }
        }

        public Task<Response> SendEmail(Message message);

        /// <summary>
        /// The ordinary case: a subject, a body and some recipients. Kept because most mail is
        /// exactly that, and because it is what every existing caller already writes.
        /// </summary>
        public Task<Response> SendEmail(string subject, string body, IEnumerable<string> recipients, IEnumerable<Attachment> attachments = null)
        {
            return SendEmail(new Message()
            {
                Subject = subject,
                Body = body,
                To = recipients,
                Attachments = attachments,
            });
        }
    }
}
