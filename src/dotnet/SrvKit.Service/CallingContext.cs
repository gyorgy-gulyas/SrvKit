using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using System.Globalization;

namespace ServiceKit.Net
{
    public class CallingContext
    {
        private const string const_calling_user_id = "calling-user-id";
        private const string const_client_language = "client-language";
        private const string const_correlation_id = "correlation-id";
        private const string const_call_stack = "call-stack";
        private const string const_tenant_id = "tenant-id";
        private const string const_client_application = "client-application";
        private const string const_client_version = "client-version";
        private const string const_client_tz_offset = "client-tz-offset";
        private const string const_gateway_version = "gateway_version";
        private const string const_claim = "claim-";

        public string CorrelationId { get; internal set; }
        public string CallStack { get; internal set; } = string.Empty;
        public string TenantId { get; internal set; }
        public Dictionary<string, string> Claims { get; internal set; }
        public ILogger Logger { get; internal set; } = NullLogger.Instance;

        public class ClientInfoData
        {
            public string CallingUserId { get; internal set; }
            public string ClientLanguage { get; internal set; }
            public string ClientApplication { get; internal set; }
            public string ClientVersion { get; internal set; }
            public int ClientTimeZoneOffset { get; internal set; }
            public int GatewayVersion { get; internal set; }
        }
        public ClientInfoData ClientInfo { get; internal set; }

        private static readonly ObjectPool<CallingContext> _pool = new DefaultObjectPool<CallingContext>(new DefaultPooledObjectPolicy<CallingContext>());

        public static CallingContext PoolFromGrpcContext(ServerCallContext @this, ILogger logger = null)
        {
            var ctx = _pool.Get();
            var metadata = @this.RequestHeaders;

            var metaMap = new Dictionary<string, string>(metadata.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in metadata)
            {
                if (entry.Value != null)
                    metaMap[entry.Key] = entry.Value;
            }

            string Get(string key) =>
                metaMap.TryGetValue(key, out var value) ? value : string.Empty;

            int GetInt(string key) =>
                int.TryParse(Get(key), out var result) ? result : 0;

            ctx.Claims = new Dictionary<string, string>(metaMap.Count);
            foreach (var kv in metaMap)
            {
                if (kv.Key.StartsWith(const_claim))
                    ctx.Claims[kv.Key.Substring(const_claim.Length)] = kv.Value;
            }

            ctx.CorrelationId = Get(const_correlation_id);
            ctx.CallStack = Get(const_call_stack);
            ctx.TenantId = Get(const_tenant_id);
            ctx.Logger = logger ?? NullLogger.Instance;

            ctx.ClientInfo ??= new ClientInfoData();
            ctx.ClientInfo.CallingUserId = Get(const_calling_user_id);
            ctx.ClientInfo.ClientLanguage = Get(const_client_language);
            ctx.ClientInfo.ClientApplication = Get(const_client_application);
            ctx.ClientInfo.ClientVersion = Get(const_client_version);
            ctx.ClientInfo.ClientTimeZoneOffset = GetInt(const_client_tz_offset);
            ctx.ClientInfo.GatewayVersion = GetInt(const_gateway_version);

            return ctx;
        }

        public static CallingContext PoolFromHttpContext(HttpContext @this, ILogger logger = null)
        {
            var ctx = _pool.Get();
            var headers = @this.Request.Headers;
            var user = @this.User;

            string Get(string key) =>
                headers.TryGetValue(key, out var value) ? value.ToString() : string.Empty;

            int GetInt(string key) =>
                int.TryParse(Get(key), out var result) ? result : 0;

            // Claims most már a felhasználói identity-ből jön
            ctx.Claims = new Dictionary<string, string>();
            if (user?.Identity?.IsAuthenticated == true)
            {
                foreach (var claim in user.Claims)
                {
                    ctx.Claims[claim.Type] = claim.Value;
                }
            }

            ctx.CorrelationId = Get(const_correlation_id);
            ctx.CallStack = Get(const_call_stack);
            ctx.TenantId = Get(const_tenant_id);
            ctx.Logger = logger ?? NullLogger.Instance;

            ctx.ClientInfo ??= new ClientInfoData();
            ctx.ClientInfo.CallingUserId = user?.Identity?.Name ?? Get(const_calling_user_id); // fallback headerre
            ctx.ClientInfo.ClientLanguage = Get(const_client_language);
            ctx.ClientInfo.ClientApplication = Get(const_client_application);
            ctx.ClientInfo.ClientVersion = Get(const_client_version);
            ctx.ClientInfo.ClientTimeZoneOffset = GetInt(const_client_tz_offset);
            ctx.ClientInfo.GatewayVersion = GetInt(const_gateway_version);

            return ctx;
        }

        public void ReturnToPool()
        {
            Claims.Clear();
            CorrelationId = string.Empty;
            CallStack = string.Empty;
            TenantId = string.Empty;
            if (ClientInfo != null)
            {
                ClientInfo.CallingUserId = null;
                ClientInfo.ClientLanguage = null;
                ClientInfo.ClientApplication = null;
                ClientInfo.ClientVersion = null;
                ClientInfo.ClientTimeZoneOffset = 0;
                ClientInfo.GatewayVersion = 0;
            }

            Logger = NullLogger.Instance;
            _pool.Return(this);
        }

        public Metadata ToGrpcMetadata(string serviceName, string methodName)
        {
            var metadata = new Metadata();

            void AddIfNotNullOrEmpty(string key, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    metadata.Add(key, value);
            }

            void AddIfNotZero(string key, int value)
            {
                if (value != 0)
                    metadata.Add(key, value.ToString(CultureInfo.InvariantCulture));
            }

            AddIfNotNullOrEmpty(const_correlation_id, CorrelationId);
            AddIfNotNullOrEmpty(const_tenant_id, TenantId);

            var newStack = string.IsNullOrWhiteSpace(CallStack)
                ? serviceName + "." + methodName
                : CallStack + " -> " + serviceName + "." + methodName;
            AddIfNotNullOrEmpty(const_call_stack, newStack);

            if (ClientInfo is not null)
            {
                AddIfNotNullOrEmpty(const_calling_user_id, ClientInfo.CallingUserId);
                AddIfNotNullOrEmpty(const_client_language, ClientInfo.ClientLanguage);
                AddIfNotNullOrEmpty(const_client_application, ClientInfo.ClientApplication);
                AddIfNotNullOrEmpty(const_client_version, ClientInfo.ClientVersion);
                AddIfNotZero(const_client_tz_offset, ClientInfo.ClientTimeZoneOffset);
                AddIfNotZero(const_gateway_version, ClientInfo.GatewayVersion);
            }

            if (Claims != null)
            {
                foreach (var kv in Claims)
                {
                    var key = const_claim + kv.Key;
                    metadata.Add(key, kv.Value);
                }
            }

            return metadata;
        }

        public void FillHttpRequest(HttpRequestMessage request, string serviceName, string methodName)
        {
            var headers = request.Headers;

            void Set(string key, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    headers.Add( key, value );
            }

            void SetInt(string key, int value)
            {
                if (value != 0)
                    headers.Add(key, value.ToString(CultureInfo.InvariantCulture));
            }

            Set(const_correlation_id, CorrelationId);
            Set(const_tenant_id, TenantId);

            var newStack = string.IsNullOrWhiteSpace(CallStack)
                ? serviceName + "." + methodName
                : CallStack + " -> " + serviceName + "." + methodName;
            Set(const_call_stack, newStack);

            if (ClientInfo is not null)
            {
                Set(const_calling_user_id, ClientInfo.CallingUserId);
                Set(const_client_language, ClientInfo.ClientLanguage);
                Set(const_client_application, ClientInfo.ClientApplication);
                Set(const_client_version, ClientInfo.ClientVersion);
                SetInt(const_client_tz_offset, ClientInfo.ClientTimeZoneOffset);
                SetInt(const_gateway_version, ClientInfo.GatewayVersion);
            }

            // -> Claims NEM kerülnek kiírásra fejlécekbe
        }
    }
}