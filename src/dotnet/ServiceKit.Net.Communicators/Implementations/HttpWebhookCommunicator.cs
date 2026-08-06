using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ServiceKit.Net.Communicators.Implementations
{
    /// <summary>
    /// Delivers a webhook over HTTP, signed.
    ///
    /// The signature is what makes the call believable to a receiver who has no other way to know
    /// who is calling: HMAC-SHA256 over the timestamp and the exact body sent, with a secret only
    /// the two ends know. The timestamp is inside the signature on purpose - without it, a delivery
    /// somebody captured can be replayed forever.
    /// </summary>
    public class HttpWebhookCommunicator : IWebhookCommunicator
    {
        public const string HttpClientName = "ServiceKit.Webhook";

        public const string SigningSecretKey = "Webhook:SigningSecret";

        // The names are the de facto convention among the products that send webhooks, so a
        // receiver written against any of them recognises these.
        public const string SignatureHeader = "webhook-signature";
        public const string TimestampHeader = "webhook-timestamp";
        public const string DeliveryIdHeader = "webhook-id";
        public const string EventTypeHeader = "webhook-event";

        private readonly ILogger<HttpWebhookCommunicator> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _signingSecret;
        private readonly bool _isConfigured;

        public HttpWebhookCommunicator(IConfiguration configuration, ILogger<HttpWebhookCommunicator> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _signingSecret = configuration[SigningSecretKey];

            _isConfigured = string.IsNullOrWhiteSpace(_signingSecret) == false;
            if (_isConfigured == false)
                _logger?.LogError("The webhook communicator is not configured, sending will fail. Set {SigningSecretKey}.", SigningSecretKey);
        }

        async Task<Response> IWebhookCommunicator.Send(IWebhookCommunicator.Delivery delivery)
        {
            if (_isConfigured == false)
            {
                return Response.Failure(
                    Statuses.InternalError,
                    "The webhook channel is not configured",
                    $"Set {SigningSecretKey} in configuration. An unsigned webhook is one the receiver cannot believe.");
            }

            if (delivery == null || string.IsNullOrWhiteSpace(delivery.Url) == true)
                return Response.Failure(Statuses.BadRequest, "The webhook has no address");

            try
            {
                var deliveryId = string.IsNullOrWhiteSpace(delivery.DeliveryId) ? Guid.NewGuid().ToString() : delivery.DeliveryId;
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                var body = JsonSerializer.Serialize(delivery.Payload);

                using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Url);
                // The exact bytes that are signed are the exact bytes that are sent - building the
                // content from the same string is what keeps that true.
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Headers.Add(DeliveryIdHeader, deliveryId);
                request.Headers.Add(TimestampHeader, timestamp);
                request.Headers.Add(SignatureHeader, _Sign(timestamp, body));

                if (string.IsNullOrWhiteSpace(delivery.EventType) == false)
                    request.Headers.Add(EventTypeHeader, delivery.EventType);

                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var response = await client.SendAsync(request).ConfigureAwait(false);

                if (response.IsSuccessStatusCode == false)
                {
                    _logger?.LogWarning("The webhook receiver answered {StatusCode} for delivery {DeliveryId}", (int)response.StatusCode, deliveryId);
                    return Response.Failure(response.StatusCode.FromHttp(), "The webhook receiver refused the delivery");
                }

                _logger?.LogInformation("Webhook {EventType} delivered, {DeliveryId}", delivery.EventType, deliveryId);
                return Response.Success();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Delivering the webhook failed");
                return Response.Failure(Statuses.InternalError, "Delivering the webhook failed", ex.Message);
            }
        }

        /// <summary>
        /// The signature a receiver recomputes to decide whether to believe the call. Public because
        /// the receiving side of a webhook is a service too, and this is the one piece both ends
        /// have to agree on exactly.
        /// </summary>
        public static string Sign(string signingSecret, string timestamp, string body)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
            var signed = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
            return "v1=" + Convert.ToHexString(signed).ToLowerInvariant();
        }

        private string _Sign(string timestamp, string body)
        {
            return Sign(_signingSecret, timestamp, body);
        }
    }
}
