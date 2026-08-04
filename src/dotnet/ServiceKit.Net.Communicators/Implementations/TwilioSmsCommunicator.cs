using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using Twilio;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace ServiceKit.Net.Communicators.Implementations
{
    public class TwilioSmsCommunicator : ISmsCommunicator
    {
        private const string AccountSidKey = "Twilio:AccountSid";
        private const string AuthTokenKey = "Twilio:AuthToken";
        private const string FromPhoneNumberKey = "Twilio:FromPhoneNumber";

        private readonly ILogger<TwilioSmsCommunicator> _logger;
        private readonly string _fromPhoneNumber;
        private readonly bool _isConfigured;

        // The account SID, the auth token and the sending number are credentials and deployment
        // detail, so they come from configuration and never from the source.
        //
        // An unconfigured SMS channel does not stop the host: not every deployment sends SMS, and
        // the sibling communicators are optional too. It is loud instead - an error at startup, and
        // an explicit failure on every send, rather than a channel that silently swallows messages.
        public TwilioSmsCommunicator(IConfiguration configuration, ILogger<TwilioSmsCommunicator> logger)
        {
            _logger = logger;

            var accountSid = configuration[AccountSidKey];
            var authToken = configuration[AuthTokenKey];
            _fromPhoneNumber = configuration[FromPhoneNumberKey];

            _isConfigured =
                string.IsNullOrWhiteSpace(accountSid) == false &&
                string.IsNullOrWhiteSpace(authToken) == false &&
                string.IsNullOrWhiteSpace(_fromPhoneNumber) == false;

            if (_isConfigured == false)
            {
                _logger?.LogError("The Twilio SMS communicator is not configured, sending will fail. Set {AccountSidKey}, {AuthTokenKey} and {FromPhoneNumberKey}.", AccountSidKey, AuthTokenKey, FromPhoneNumberKey);
                return;
            }

            // Init installs a process wide default client, so it belongs here - this type is a
            // singleton - and not on every single message.
            TwilioClient.Init(accountSid, authToken);
        }

        Task<Response> ISmsCommunicator.SendSMS(string toPhoneNumber, string messageText)
        {
            if (_isConfigured == false)
            {
                return Response.Failure(
                    Statuses.InternalError,
                    "The SMS channel is not configured",
                    $"Set {AccountSidKey}, {AuthTokenKey} and {FromPhoneNumberKey} in configuration.").AsTask();
            }

            try
            {
                var message = MessageResource.Create(
                    to: new PhoneNumber(toPhoneNumber),
                    from: new PhoneNumber(_fromPhoneNumber),
                    body: messageText
                );

                // The recipient's number is personal data and the body may carry a one time code,
                // so neither is logged - only the identifier that lets Twilio be asked about it.
                _logger?.LogInformation("SMS sent, SID {MessageSid}", message.Sid);
                return Response.Success().AsTask();
            }
            catch (ApiException apiEx)
            {
                // Twilio reports an HTTP status, so a bad number and an expired token no longer look
                // the same to the caller - the house mapping turns it into the right one.
                return Response.Failure(
                    ((HttpStatusCode)apiEx.Status).FromHttp(),
                    apiEx.Message,
                    $"Twilio API error {apiEx.Code}").AsTask();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Sending an SMS failed");
                return Response.Failure(Statuses.InternalError, ex.Message).AsTask();
            }
        }
    }
}
