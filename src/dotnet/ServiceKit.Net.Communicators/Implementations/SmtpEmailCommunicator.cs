using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ServiceKit.Net.Communicators.Implementations
{
    /// <summary>
    /// E-mail over SMTP.
    ///
    /// This is the channel that works everywhere: a relay in the cluster, a provider's smarthost, a
    /// catcher on a developer's machine. It is the default recommendation for that reason - a
    /// channel that cannot be exercised outside production is a channel nobody exercises.
    /// </summary>
    public class SmtpEmailCommunicator : IEmailCommunicator
    {
        private const string HostKey = "Smtp:Host";
        private const string PortKey = "Smtp:Port";
        private const string UserNameKey = "Smtp:UserName";
        private const string PasswordKey = "Smtp:Password";
        private const string FromKey = "Smtp:From";
        private const string FromDisplayNameKey = "Smtp:FromDisplayName";
        private const string SecurityKey = "Smtp:Security";

        private readonly ILogger<SmtpEmailCommunicator> _logger;
        private readonly string _host;
        private readonly int _port;
        private readonly string _userName;
        private readonly string _password;
        private readonly string _from;
        private readonly string _fromDisplayName;
        private readonly SecureSocketOptions _security;
        private readonly bool _isConfigured;

        // The host, the credentials and the sender are deployment detail, so they come from
        // configuration and never from the source.
        //
        // An unconfigured mail channel does not stop the host - not every deployment sends mail. It
        // is loud instead: an error at startup and an explicit failure on every send, rather than a
        // channel that silently swallows messages, which is exactly what the stub this replaces did.
        public SmtpEmailCommunicator(IConfiguration configuration, ILogger<SmtpEmailCommunicator> logger)
        {
            _logger = logger;

            _host = configuration[HostKey];
            _userName = configuration[UserNameKey];
            _password = configuration[PasswordKey];
            _from = configuration[FromKey];
            _fromDisplayName = configuration[FromDisplayNameKey];

            _port = int.TryParse(configuration[PortKey], out var port) ? port : 25;

            // Auto is right almost always: it starts TLS when the server offers it. The value is
            // there for the servers that do not announce it, and for a local catcher that has none.
            _security = Enum.TryParse<SecureSocketOptions>(configuration[SecurityKey], ignoreCase: true, out var security)
                ? security
                : SecureSocketOptions.Auto;

            _isConfigured =
                string.IsNullOrWhiteSpace(_host) == false &&
                string.IsNullOrWhiteSpace(_from) == false;

            if (_isConfigured == false)
                _logger?.LogError("The SMTP e-mail communicator is not configured, sending will fail. Set {HostKey} and {FromKey}.", HostKey, FromKey);
        }

        async Task<Response> IEmailCommunicator.SendEmail(IEmailCommunicator.Message message)
        {
            if (_isConfigured == false)
            {
                return Response.Failure(
                    Statuses.InternalError,
                    "The e-mail channel is not configured",
                    $"Set {HostKey} and {FromKey} in configuration.");
            }

            if (message == null || message.HasRecipient() == false)
                return Response.Failure(Statuses.BadRequest, "The e-mail has no recipient");

            try
            {
                var mime = _Build(message);

                using (var client = new SmtpClient())
                {
                    await client.ConnectAsync(_host, _port, _security).ConfigureAwait(false);

                    // Only when there is something to authenticate WITH: a relay inside the cluster
                    // usually takes mail from its own network without credentials, and offering an
                    // empty user name to it is an error rather than a no-op.
                    if (string.IsNullOrWhiteSpace(_userName) == false)
                        await client.AuthenticateAsync(_userName, _password).ConfigureAwait(false);

                    await client.SendAsync(mime).ConfigureAwait(false);
                    await client.DisconnectAsync(true).ConfigureAwait(false);
                }

                // The addresses are personal data and the body may carry a one time code, so neither
                // is logged - only what lets this send be found again.
                _logger?.LogInformation("E-mail sent over SMTP, {RecipientCount} recipient(s), subject length {SubjectLength}",
                    message.AllRecipients().Count(), message.Subject?.Length ?? 0);

                return Response.Success();
            }
            catch (AuthenticationException ex)
            {
                // Told apart from a rejected address on purpose: one is ours to fix, the other is the
                // caller's.
                _logger?.LogError(ex, "The SMTP server refused the credentials");
                return Response.Failure(Statuses.InternalError, "The SMTP server refused the credentials", ex.Message);
            }
            catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted || ex.ErrorCode == SmtpErrorCode.SenderNotAccepted)
            {
                _logger?.LogWarning(ex, "The SMTP server rejected an address, {ErrorCode}", ex.ErrorCode);
                return Response.Failure(Statuses.BadRequest, "The SMTP server rejected an address", ex.Message);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Sending the e-mail over SMTP failed");
                return Response.Failure(Statuses.InternalError, "Sending the e-mail failed", ex.Message);
            }
        }

        private MimeMessage _Build(IEmailCommunicator.Message message)
        {
            var mime = new MimeMessage();

            var from = string.IsNullOrWhiteSpace(message.From) ? _from : message.From;
            var fromDisplayName = string.IsNullOrWhiteSpace(message.FromDisplayName) ? _fromDisplayName : message.FromDisplayName;
            mime.From.Add(new MailboxAddress(fromDisplayName ?? string.Empty, from));

            if (string.IsNullOrWhiteSpace(message.ReplyTo) == false)
                mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));

            _AddAll(mime.To, message.To);
            _AddAll(mime.Cc, message.Cc);
            // Bcc is added to the envelope by MailKit and is deliberately NOT written into the
            // headers - that is what makes it blind.
            _AddAll(mime.Bcc, message.Bcc);

            mime.Subject = message.Subject ?? string.Empty;

            var builder = new BodyBuilder();
            if (message.BodyFormat == IEmailCommunicator.BodyFormats.Html)
                builder.HtmlBody = message.Body;
            else
                builder.TextBody = message.Body;

            if (message.Attachments != null)
            {
                foreach (var attachment in message.Attachments)
                {
                    if (attachment?.Content == null)
                        continue;

                    var contentType = string.IsNullOrWhiteSpace(attachment.ContentType)
                        ? "application/octet-stream"
                        : attachment.ContentType;

                    if (string.IsNullOrWhiteSpace(attachment.ContentId) == false)
                    {
                        // part of the message, referenced from the markup - not a file to save
                        var linked = builder.LinkedResources.Add(attachment.FileName, attachment.Content, ContentType.Parse(contentType));
                        linked.ContentId = attachment.ContentId;
                    }
                    else
                    {
                        builder.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(contentType));
                    }
                }
            }

            mime.Body = builder.ToMessageBody();
            return mime;
        }

        private static void _AddAll(InternetAddressList list, IEnumerable<string> addresses)
        {
            if (addresses == null)
                return;

            foreach (var address in addresses)
            {
                if (string.IsNullOrWhiteSpace(address) == false)
                    list.Add(MailboxAddress.Parse(address));
            }
        }
    }
}
