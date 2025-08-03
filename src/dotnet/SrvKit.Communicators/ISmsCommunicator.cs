using ServiceKit.Net;

namespace ServiceKit.Net.Communicators
{
    public interface ISmsCommunicator
    {
        public Response SendSMS(string toPhoneNumber, string messageText);
    }
}
