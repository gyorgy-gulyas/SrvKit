using Microsoft.Extensions.DependencyInjection;
using ServiceKit.Net.Communicators;
using SrvKit.Communicators.Implementations;

namespace SrvKit.Communicators
{
    public static class ServiceCollectionExtension
    {
        public static void UseSms_Twilio(this IServiceCollection services)
        {
            services.AddSingleton<ISmsCommunicator, TwilioSmsCommunicator>();
        }

        public static void UseEmail_Graph(this IServiceCollection services)
        {
            services.AddSingleton<IEmailCommunicator, GraphEmailCommunicator>();
        }
    }
}
