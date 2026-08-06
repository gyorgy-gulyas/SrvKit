using Microsoft.Extensions.DependencyInjection;
using ServiceKit.Net.Communicators.Implementations;

namespace ServiceKit.Net.Communicators
{
    /// <summary>
    /// Which channel carries the message is a deployment decision, so it is made here - once, at
    /// registration - and nothing downstream knows which one it got.
    /// </summary>
    public static class ServiceCollectionExtension
    {
        public static void UseSms_Twilio(this IServiceCollection services)
        {
            services.AddSingleton<ISmsCommunicator, TwilioSmsCommunicator>();
        }

        /// <summary>
        /// E-mail over SMTP - the one that works everywhere, including a catcher on a developer's
        /// machine.
        /// </summary>
        public static void UseEmail_Smtp(this IServiceCollection services)
        {
            services.AddSingleton<IEmailCommunicator, SmtpEmailCommunicator>();
        }

        /// <summary>
        /// E-mail through Microsoft Graph, on behalf of a mailbox in the tenant.
        /// </summary>
        public static void UseEmail_Graph(this IServiceCollection services)
        {
            // The communicator is a singleton and the client comes from the factory per send, which
            // is what keeps the sockets pooled and the DNS honoured.
            //
            // With the house resilience pipeline, because Graph is a remote service that is
            // occasionally busy and sendMail is worth one more try. The retry stays off for the POST
            // itself - see AddServiceKitResilience - so a message is never sent twice.
            services.AddHttpClient(GraphEmailCommunicator.HttpClientName)
                .AddServiceKitResilience();
            services.AddSingleton<IEmailCommunicator, GraphEmailCommunicator>();
        }
    }
}
