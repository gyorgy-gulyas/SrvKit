using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ServiceKit.Net.Communicators.Implementations
{
    /// <summary>
    /// E-mail through Microsoft Graph, on behalf of a mailbox in the tenant.
    ///
    /// This used to answer Success() without calling anything - a channel that swallowed every
    /// message it was given.
    ///
    /// It talks to the sendMail endpoint over plain HTTP rather than through the Graph SDK. The SDK
    /// carries a generated model of the entire Graph API for the sake of one POST, and a platform
    /// library is referenced by every service the platform has. The token still comes from
    /// Azure.Identity, which is the part worth not writing by hand.
    /// </summary>
    public class GraphEmailCommunicator : IEmailCommunicator
    {
        public const string HttpClientName = "ServiceKit.Graph";

        private const string TenantIdKey = "Graph:TenantId";
        private const string ClientIdKey = "Graph:ClientId";
        private const string ClientSecretKey = "Graph:ClientSecret";
        private const string FromKey = "Graph:From";
        private const string SaveToSentItemsKey = "Graph:SaveToSentItems";
        private const string BaseAddressKey = "Graph:BaseAddress";

        private const string DefaultBaseAddress = "https://graph.microsoft.com/v1.0/";
        private static readonly string[] _scopes = new[] { "https://graph.microsoft.com/.default" };

        private readonly ILogger<GraphEmailCommunicator> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly TokenCredential _credential;
        private readonly string _from;
        private readonly string _baseAddress;
        private readonly bool _saveToSentItems;
        private readonly bool _isConfigured;

        public GraphEmailCommunicator(IConfiguration configuration, ILogger<GraphEmailCommunicator> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;

            var tenantId = configuration[TenantIdKey];
            var clientId = configuration[ClientIdKey];
            var clientSecret = configuration[ClientSecretKey];
            _from = configuration[FromKey];

            _baseAddress = configuration[BaseAddressKey];
            if (string.IsNullOrWhiteSpace(_baseAddress) == true)
                _baseAddress = DefaultBaseAddress;

            // Off by default: a service account's Sent Items is a copy of every notification the
            // system has ever sent, and nobody reads it.
            _saveToSentItems = bool.TryParse(configuration[SaveToSentItemsKey], out var save) && save;

            _isConfigured =
                string.IsNullOrWhiteSpace(tenantId) == false &&
                string.IsNullOrWhiteSpace(clientId) == false &&
                string.IsNullOrWhiteSpace(clientSecret) == false &&
                string.IsNullOrWhiteSpace(_from) == false;

            if (_isConfigured == false)
            {
                _logger?.LogError("The Graph e-mail communicator is not configured, sending will fail. Set {TenantIdKey}, {ClientIdKey}, {ClientSecretKey} and {FromKey}.",
                    TenantIdKey, ClientIdKey, ClientSecretKey, FromKey);
                return;
            }

            _credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        }

        async Task<Response> IEmailCommunicator.SendEmail(IEmailCommunicator.Message message)
        {
            if (_isConfigured == false)
            {
                return Response.Failure(
                    Statuses.InternalError,
                    "The e-mail channel is not configured",
                    $"Set {TenantIdKey}, {ClientIdKey}, {ClientSecretKey} and {FromKey} in configuration.");
            }

            if (message == null || message.HasRecipient() == false)
                return Response.Failure(Statuses.BadRequest, "The e-mail has no recipient");

            try
            {
                var token = await AcquireToken(CancellationToken.None).ConfigureAwait(false);

                var client = _httpClientFactory.CreateClient(HttpClientName);
                if (client.BaseAddress == null)
                    client.BaseAddress = new Uri(_baseAddress);

                var sender = string.IsNullOrWhiteSpace(message.From) ? _from : message.From;

                using var request = new HttpRequestMessage(HttpMethod.Post, $"users/{Uri.EscapeDataString(sender)}/sendMail");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = JsonContent.Create(_Build(message));

                using var response = await client.SendAsync(request).ConfigureAwait(false);

                if (response.IsSuccessStatusCode == false)
                {
                    // Graph answers with a JSON error body worth keeping: "the mailbox does not
                    // exist" and "the application has no permission" are different problems and used
                    // to look identical from here.
                    var detail = response.Content != null
                        ? await response.Content.ReadAsStringAsync().ConfigureAwait(false)
                        : string.Empty;

                    _logger?.LogError("Graph refused the message, {StatusCode}", (int)response.StatusCode);
                    return Response.Failure(response.StatusCode.FromHttp(), "Graph refused the message", detail);
                }

                // The addresses are personal data and the body may carry a one time code, so neither
                // is logged.
                _logger?.LogInformation("E-mail sent through Graph, {RecipientCount} recipient(s), subject length {SubjectLength}",
                    message.AllRecipients().Count(), message.Subject?.Length ?? 0);

                return Response.Success();
            }
            catch (AuthenticationFailedException ex)
            {
                // Told apart from a rejected message on purpose: this one is ours to fix.
                _logger?.LogError(ex, "Acquiring the Graph token failed");
                return Response.Failure(Statuses.InternalError, "Acquiring the Graph token failed", ex.Message);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Sending the e-mail through Graph failed");
                return Response.Failure(Statuses.InternalError, "Sending the e-mail failed", ex.Message);
            }
        }

        /// <summary>
        /// The bearer token for the call. Overridable so the request itself can be tested without a
        /// tenant to get a real one from.
        /// </summary>
        protected virtual async Task<string> AcquireToken(CancellationToken cancellationToken)
        {
            var token = await _credential.GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken).ConfigureAwait(false);
            return token.Token;
        }

        // The shape Graph expects. Built by hand because it is one request, and a hand-built object
        // is easier to read than a generated model of an API this uses one endpoint of.
        private object _Build(IEmailCommunicator.Message message)
        {
            var graphMessage = new Dictionary<string, object>()
            {
                ["subject"] = message.Subject ?? string.Empty,
                ["body"] = new Dictionary<string, object>()
                {
                    ["contentType"] = message.BodyFormat == IEmailCommunicator.BodyFormats.Html ? "HTML" : "Text",
                    ["content"] = message.Body ?? string.Empty,
                },
                ["toRecipients"] = _Recipients(message.To),
                ["ccRecipients"] = _Recipients(message.Cc),
                ["bccRecipients"] = _Recipients(message.Bcc),
            };

            if (string.IsNullOrWhiteSpace(message.ReplyTo) == false)
                graphMessage["replyTo"] = _Recipients(new[] { message.ReplyTo });

            var attachments = _Attachments(message.Attachments);
            if (attachments.Count > 0)
                graphMessage["attachments"] = attachments;

            return new Dictionary<string, object>()
            {
                ["message"] = graphMessage,
                ["saveToSentItems"] = _saveToSentItems,
            };
        }

        private static List<object> _Recipients(IEnumerable<string> addresses)
        {
            var recipients = new List<object>();
            if (addresses == null)
                return recipients;

            foreach (var address in addresses)
            {
                if (string.IsNullOrWhiteSpace(address) == true)
                    continue;

                recipients.Add(new Dictionary<string, object>()
                {
                    ["emailAddress"] = new Dictionary<string, object>() { ["address"] = address },
                });
            }

            return recipients;
        }

        private static List<object> _Attachments(IEnumerable<IEmailCommunicator.Attachment> attachments)
        {
            var built = new List<object>();
            if (attachments == null)
                return built;

            foreach (var attachment in attachments)
            {
                if (attachment?.Content == null)
                    continue;

                var file = new Dictionary<string, object>()
                {
                    ["@odata.type"] = "#microsoft.graph.fileAttachment",
                    ["name"] = attachment.FileName ?? "attachment",
                    ["contentType"] = string.IsNullOrWhiteSpace(attachment.ContentType) ? "application/octet-stream" : attachment.ContentType,
                    ["contentBytes"] = Convert.ToBase64String(attachment.Content),
                };

                // An attachment with a content id is referenced from the markup and is part of the
                // message; one without it is a file the reader can save.
                if (string.IsNullOrWhiteSpace(attachment.ContentId) == false)
                {
                    file["contentId"] = attachment.ContentId;
                    file["isInline"] = true;
                }

                built.Add(file);
            }

            return built;
        }
    }
}
