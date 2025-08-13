using ServiceKit.Net;
using ServiceKit.Net.Communicators;

namespace SrvKit.Communicators.Implementations
{
    public class GraphEmailCommunicator: IEmailCommunicator
    {
        Task<Response> IEmailCommunicator.SendEmail(string subject, string body, IEnumerable<string> recipients, IEnumerable<IEmailCommunicator.Attachment> attachments)
        {
            return Response.Success().AsTask();
        }
    }
}
