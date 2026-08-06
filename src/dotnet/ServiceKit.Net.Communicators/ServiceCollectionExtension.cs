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
        /// <summary>
        /// Push through a gateway that speaks HTTP - Firebase, Expo, a house relay. The vendor's own
        /// SDK, if a deployment wants one, replaces the implementation rather than configures it.
        /// </summary>
        public static void UsePush_HttpGateway(this IServiceCollection services)
        {
            services.AddHttpClient(HttpPushCommunicator.HttpClientName)
                .AddServiceKitResilience();

            services.AddSingleton<IPushCommunicator, HttpPushCommunicator>();
        }

        /// <summary>
        /// Signed webhooks over HTTP.
        ///
        /// This is the one channel whose retry is turned ON for the POST: a delivery carries an id,
        /// so a receiver that has seen it can drop it - which is precisely the condition under which
        /// repeating a POST is safe.
        /// </summary>
        public static void UseWebhook_Http(this IServiceCollection services)
        {
            services.AddHttpClient(HttpWebhookCommunicator.HttpClientName)
                .AddServiceKitResilience(options => options.RetryUnsafeMethods = true);

            services.AddSingleton<IWebhookCommunicator, HttpWebhookCommunicator>();
        }

        /// <summary>
        /// In-app notifications kept in this process - for development and tests. Where unread
        /// notifications really live is a product decision, so a product replaces this and nothing
        /// calling it changes.
        /// </summary>
        public static void UseInnerNotification_InMemory(this IServiceCollection services)
        {
            services.AddSingleton<IInnerNotificationCommunicator, InMemoryInnerNotificationCommunicator>();
        }

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
