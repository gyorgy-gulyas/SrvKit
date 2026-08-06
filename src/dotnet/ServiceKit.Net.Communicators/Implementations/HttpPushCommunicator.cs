using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ServiceKit.Net.Communicators.Implementations
{
    /// <summary>
    /// Push through a gateway that speaks HTTP.
    ///
    /// Deliberately not tied to one vendor. Firebase, Expo, a house relay and everything else in
    /// this space take the same three things - some device tokens, something to show, something to
    /// carry - and differ only in the envelope. Point it at the endpoint and it sends; the vendor's
    /// own SDK, if a deployment wants one, replaces this class rather than configures it.
    /// </summary>
    public class HttpPushCommunicator : IPushCommunicator
    {
        public const string HttpClientName = "ServiceKit.Push";

        private const string EndpointKey = "Push:Endpoint";
        private const string ApiKeyKey = "Push:ApiKey";

        private readonly ILogger<HttpPushCommunicator> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _endpoint;
        private readonly string _apiKey;
        private readonly bool _isConfigured;

        public HttpPushCommunicator(IConfiguration configuration, ILogger<HttpPushCommunicator> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;

            _endpoint = configuration[EndpointKey];
            _apiKey = configuration[ApiKeyKey];

            _isConfigured = string.IsNullOrWhiteSpace(_endpoint) == false;
            if (_isConfigured == false)
                _logger?.LogError("The push communicator is not configured, sending will fail. Set {EndpointKey}.", EndpointKey);
        }

        async Task<Response> IPushCommunicator.Send(IPushCommunicator.Notification notification)
        {
            if (_isConfigured == false)
            {
                return Response.Failure(
                    Statuses.InternalError,
                    "The push channel is not configured",
                    $"Set {EndpointKey} in configuration.");
            }

            if (notification == null || notification.HasRecipient() == false)
                return Response.Failure(Statuses.BadRequest, "The notification has no device to go to");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                request.Content = JsonContent.Create(new
                {
                    tokens = notification.DeviceTokens.Where(token => string.IsNullOrWhiteSpace(token) == false).ToArray(),
                    title = notification.Title ?? string.Empty,
                    body = notification.Body ?? string.Empty,
                    data = notification.Data ?? new Dictionary<string, string>(),
                });

                if (string.IsNullOrWhiteSpace(_apiKey) == false)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var response = await client.SendAsync(request).ConfigureAwait(false);

                if (response.IsSuccessStatusCode == false)
                {
                    var detail = response.Content != null ? await response.Content.ReadAsStringAsync().ConfigureAwait(false) : string.Empty;
                    _logger?.LogError("The push gateway refused the notification, {StatusCode}", (int)response.StatusCode);
                    return Response.Failure(response.StatusCode.FromHttp(), "The push gateway refused the notification", detail);
                }

                // A device token identifies somebody's phone, so it is counted and not written down.
                _logger?.LogInformation("Push sent to {DeviceCount} device(s)", notification.DeviceTokens.Count());
                return Response.Success();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Sending the push notification failed");
                return Response.Failure(Statuses.InternalError, "Sending the push notification failed", ex.Message);
            }
        }
    }
}
