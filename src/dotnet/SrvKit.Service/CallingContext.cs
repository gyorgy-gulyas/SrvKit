using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using System.Globalization;

namespace ServiceKit.Net
{
    public class CallingContext : ICloneable
    {
        public enum IdentityTypes
        {
            User,
            Service,
            Unknown,
        }

        public string CorrelationId { get; internal set; }
        public string CallStack { get; internal set; } = string.Empty;
        public string TenantId { get; internal set; }
        public string IdentityId { get; internal set; }
        public string IdentityName { get; internal set; }
        public IdentityTypes IdentityType { get; internal set; }
        public Dictionary<string, string> Claims { get; internal set; }
        public ILogger Logger { get; internal set; } = NullLogger.Instance;

        public class ClientInfoData
        {
            public string ClientLanguage { get; internal set; }
            public string ClientApplication { get; internal set; }
            public string ClientVersion { get; internal set; }
            public int ClientTimeZoneOffset { get; internal set; }
            public int ApiClientKitVersion { get; internal set; }
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
                if (kv.Key.StartsWith(ServiceConstans.const_claim))
                    ctx.Claims[kv.Key.Substring(ServiceConstans.const_claim.Length)] = kv.Value;
            }

            ctx.CorrelationId = Get(ServiceConstans.const_correlation_id);
            ctx.CallStack = Get(ServiceConstans.const_call_stack);
            ctx.TenantId = Get(ServiceConstans.const_tenant_id);
            ctx.IdentityId = Get(ServiceConstans.const_identity_id);
            ctx.IdentityName = Get(ServiceConstans.const_identity_name);
            ctx.IdentityType = Enum.TryParse<IdentityTypes>(Get(ServiceConstans.const_identity_type), out var identityType) ? identityType : IdentityTypes.Unknown;
            ctx.Logger = logger ?? NullLogger.Instance;

            ctx.ClientInfo ??= new ClientInfoData();
            ctx.ClientInfo.ClientLanguage = Get(ServiceConstans.const_client_language);
            ctx.ClientInfo.ClientApplication = Get(ServiceConstans.const_client_application);
            ctx.ClientInfo.ClientVersion = Get(ServiceConstans.const_client_version);
            ctx.ClientInfo.ClientTimeZoneOffset = GetInt(ServiceConstans.const_client_tz_offset);
            ctx.ClientInfo.ApiClientKitVersion = GetInt(ServiceConstans.const_api_client_kit_version);

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

            ctx.CorrelationId = Get(ServiceConstans.const_correlation_id);
            ctx.CallStack = Get(ServiceConstans.const_call_stack);
            ctx.TenantId = Get(ServiceConstans.const_tenant_id);
            ctx.IdentityId = Get(ServiceConstans.const_identity_id);
            ctx.IdentityName = Get(ServiceConstans.const_identity_name);
            ctx.IdentityType = Enum.TryParse<IdentityTypes>(Get(ServiceConstans.const_identity_type), out var identityType) ? identityType : IdentityTypes.Unknown;
            ctx.Logger = logger ?? NullLogger.Instance;

            ctx.ClientInfo ??= new ClientInfoData();
            ctx.ClientInfo.ClientLanguage = Get(ServiceConstans.const_client_language);
            ctx.ClientInfo.ClientApplication = Get(ServiceConstans.const_client_application);
            ctx.ClientInfo.ClientVersion = Get(ServiceConstans.const_client_version);
            ctx.ClientInfo.ClientTimeZoneOffset = GetInt(ServiceConstans.const_client_tz_offset);
            ctx.ClientInfo.ApiClientKitVersion = GetInt(ServiceConstans.const_api_client_kit_version);

            return ctx;
        }

        public void ReturnToPool()
        {
            Claims.Clear();
            CorrelationId = string.Empty;
            CallStack = string.Empty;
            TenantId = string.Empty;
            IdentityId = string.Empty;
            IdentityName = string.Empty;
            if (ClientInfo != null)
            {
                ClientInfo.ClientLanguage = null;
                ClientInfo.ClientApplication = null;
                ClientInfo.ClientVersion = null;
                ClientInfo.ClientTimeZoneOffset = 0;
                ClientInfo.ApiClientKitVersion = 0;
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

            AddIfNotNullOrEmpty(ServiceConstans.const_correlation_id, CorrelationId);
            AddIfNotNullOrEmpty(ServiceConstans.const_tenant_id, TenantId);
            AddIfNotNullOrEmpty(ServiceConstans.const_identity_id, IdentityId);
            AddIfNotNullOrEmpty(ServiceConstans.const_identity_name, IdentityName);
            AddIfNotNullOrEmpty(ServiceConstans.const_identity_type, IdentityType.ToString());

            var newStack = string.IsNullOrWhiteSpace(CallStack)
                ? serviceName + "." + methodName
                : CallStack + " -> " + serviceName + "." + methodName;
            AddIfNotNullOrEmpty(ServiceConstans.const_call_stack, newStack);

            if (ClientInfo is not null)
            {
                AddIfNotNullOrEmpty(ServiceConstans.const_client_language, ClientInfo.ClientLanguage);
                AddIfNotNullOrEmpty(ServiceConstans.const_client_application, ClientInfo.ClientApplication);
                AddIfNotNullOrEmpty(ServiceConstans.const_client_version, ClientInfo.ClientVersion);
                AddIfNotZero(ServiceConstans.const_client_tz_offset, ClientInfo.ClientTimeZoneOffset);
                AddIfNotZero(ServiceConstans.const_api_client_kit_version, ClientInfo.ApiClientKitVersion);
            }

            if (Claims != null)
            {
                foreach (var kv in Claims)
                {
                    var key = ServiceConstans.const_claim + kv.Key;
                    metadata.Add(key, kv.Value);
                }
            }

            return metadata;
        }

        public void FillHttpRequest(HttpRequestMessage request, string serviceName, string methodName)
        {
            var headers = request.Headers;
            headers.Add("x-request-id", Guid.NewGuid().ToString());

            void Set(string key, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    headers.Add(key, value);
            }

            void SetInt(string key, int value)
            {
                if (value != 0)
                    headers.Add(key, value.ToString(CultureInfo.InvariantCulture));
            }

            Set(ServiceConstans.const_correlation_id, CorrelationId);
            Set(ServiceConstans.const_tenant_id, TenantId);
            Set(ServiceConstans.const_identity_id, IdentityId);
            Set(ServiceConstans.const_identity_name, IdentityName);
            Set(ServiceConstans.const_identity_type, IdentityType.ToString());

            var newStack = string.IsNullOrWhiteSpace(CallStack)
                ? serviceName + "." + methodName
                : CallStack + " -> " + serviceName + "." + methodName;
            Set(ServiceConstans.const_call_stack, newStack);

            if (ClientInfo is not null)
            {
                Set(ServiceConstans.const_client_language, ClientInfo.ClientLanguage);
                Set(ServiceConstans.const_client_application, ClientInfo.ClientApplication);
                Set(ServiceConstans.const_client_version, ClientInfo.ClientVersion);
                SetInt(ServiceConstans.const_client_tz_offset, ClientInfo.ClientTimeZoneOffset);
                SetInt(ServiceConstans.const_api_client_kit_version, ClientInfo.ApiClientKitVersion);
            }

            // -> Claims NEM kerülnek kiírásra fejlécekbe
        }

        object ICloneable.Clone() => CloneWithIdentity( IdentityId, IdentityName, IdentityType );

        public CallingContext CloneWithIdentity(string identityId, string identityName, IdentityTypes identityType)
        {
            CallingContext clone = new()
            {
                CorrelationId = CorrelationId,
                CallStack = CallStack,
                TenantId = TenantId,
                IdentityId = identityId,
                IdentityName = identityName,
                IdentityType = identityType,
                Logger = Logger,
                ClientInfo = ClientInfo == null
                    ? null
                    : new ClientInfoData()
                    {
                        ClientLanguage = ClientInfo.ClientLanguage,
                        ClientApplication = ClientInfo.ClientApplication,
                        ClientVersion = ClientInfo.ClientVersion,
                        ClientTimeZoneOffset = ClientInfo.ClientTimeZoneOffset,
                        ApiClientKitVersion = ClientInfo.ApiClientKitVersion,
                    }
            };
            return clone;
        }
    }
}