using ServiceKit.Net;
using ServiceKit.Net.Communicators;
using Twilio;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace SrvKit.Communicators.Implementations
{
    public class TwilioSmsCommunicator : ISmsCommunicator
    {
        public readonly string _twilioAccountSid = "TWILIO_ACCOUNT_SID";
        public readonly string _twilioAuthToken = "TWILIO_AUTH_TOKEN";
        public readonly string _twilioFromPhoneNumber = "+1234567890";

        Task<Response> ISmsCommunicator.SendSMS(string toPhoneNumber, string messageText)
        {
            try
            {
                TwilioClient.Init(_twilioAccountSid, _twilioAuthToken);

                var message = MessageResource.Create(
                    to: new PhoneNumber(toPhoneNumber),
                    from: new PhoneNumber(_twilioFromPhoneNumber),
                    body: messageText
                );

                Console.WriteLine($"✅ SMS sent (SID: {message.Sid})");
                return Response.Success().AsTask();
            }
            catch (ApiException apiEx)
            {
                return Response.Failure(new Error()
                {
                    Status = Statuses.BadRequest,
                    MessageText = apiEx.Message,
                    AdditionalInformation = $"❌ Twilio API error: {apiEx.Message} (code: {apiEx.Code})"
                }).AsTask();
            }
            catch (Exception ex)
            {
                return Response.Failure(new Error()
                {
                    Status = Statuses.BadRequest,
                    MessageText = ex.Message,
                }).AsTask();
            }
        }
    }
}
