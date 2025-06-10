using Microsoft.Extensions.Logging;

namespace ServiceKit.Net
{
    public class CallingContext
    {
        public string CorrelationId { get; init; }
        public string TenantId { get; init; }
        public Dictionary<string, string> Claims { get; init; }
        public ILogger Logger { get; init; }

        public class ClientInfoData
        {
            public string CallingUserId { get; init; }
            public string ClientLanguage { get; init; }
            public string ClientApplication { get; init; }
            public string ClientVersion { get; init; }
            public int ClientTimeZoneOffset { get; init; }
            public int GatewayVersion { get; init; }
        }
        public ClientInfoData ClientInfo { get; init; }

        public class ServiceInfoData
        {
            public string CallingServiceId { get; init; }
        }
        public ServiceInfoData ServiceInfo { get; init; }
    }
}