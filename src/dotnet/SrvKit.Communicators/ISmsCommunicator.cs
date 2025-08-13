using ServiceKit.Net;

namespace ServiceKit.Net.Communicators
{
    public interface ISmsCommunicator
    {
        public Task<Response> SendSMS(string toPhoneNumber, string messageText);
    }
}
