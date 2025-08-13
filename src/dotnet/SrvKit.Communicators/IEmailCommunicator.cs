namespace ServiceKit.Net.Communicators
{
    public interface IEmailCommunicator
    {
        public class Attachment
        {
            public byte[] Content;
            public string ContentType;
            public string FileName;
        }

        public Task<Response> SendEmail(string subject, string body, IEnumerable<string> recipients, IEnumerable<Attachment> attachments = null);
    }
}
