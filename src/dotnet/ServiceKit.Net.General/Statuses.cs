using Microsoft.AspNetCore.Http;
using System.Net;

namespace ServiceKit.Net
{
    // The numbers must stay in step with the Statuses enum in Protos/error.proto - existing values
    // are never renumbered, new ones are appended.
    public enum Statuses
    {
        /// gRPC: OK (0), Http: 200 (Ok)
        Ok = 0,
        /// gRPC: INVALID_ARGUMENT (3), Http: 400 (Bad Request)
        BadRequest = 1,
        /// gRPC: DEADLINE_EXCEEDED (4), Http: 504 (Gateway Timeout). Retryable.
        Timeout = 2,
        /// gRPC: NOT_FOUND (5), Http: 404 (Not Found)
        NotFound = 3,
        /// Not authenticated - who are you? gRPC: UNAUTHENTICATED (16), Http: 401 (Unauthorized)
        Unauthorized = 4,
        /// gRPC: UNIMPLEMENTED (12), Http: 501 (Not Implemented)
        NotImplemented = 5,
        /// gRPC: INTERNAL (13), Http: 500 (Internal Server Error)
        InternalError = 6,
        /// Authenticated, but not allowed. A different answer from Unauthorized, because retrying
        /// with a fresh token helps in one case and never in the other.
        /// gRPC: PERMISSION_DENIED (7), Http: 403 (Forbidden)
        Forbidden = 7,
        /// gRPC: RESOURCE_EXHAUSTED (8), Http: 429 (Too Many Requests). Retryable.
        TooManyRequests = 8,
        /// gRPC: UNAVAILABLE (14), Http: 503 (Service Unavailable). Retryable.
        Unavailable = 9,
    }

    /// <summary>
    /// Translations between the service status and the transports.
    ///
    /// Each status has ONE canonical HTTP code and ONE canonical gRPC code, and the inbound
    /// direction maps those back to the same status - so a status survives a hop and comes back
    /// unchanged. The inbound maps accept more than the canonical code, because a proxy or a
    /// foreign service may answer with anything, but nothing else is many-to-one.
    /// </summary>
    public static class StatusesExtensions
    {
        public static Protos.Statuses ToGrpc(this Statuses @this)
        {
            return @this switch
            {
                Statuses.Ok => Protos.Statuses.Ok,
                Statuses.BadRequest => Protos.Statuses.BadRequest,
                Statuses.Timeout => Protos.Statuses.Timeout,
                Statuses.NotFound => Protos.Statuses.NotFound,
                Statuses.Unauthorized => Protos.Statuses.Unauthorized,
                Statuses.NotImplemented => Protos.Statuses.NotImplemented,
                Statuses.InternalError => Protos.Statuses.InternalError,
                Statuses.Forbidden => Protos.Statuses.Forbidden,
                Statuses.TooManyRequests => Protos.Statuses.TooManyRequests,
                Statuses.Unavailable => Protos.Statuses.Unavailable,
                _ => Protos.Statuses.InternalError,
            };
        }

        public static Statuses FromGrpc(this Protos.Statuses @this)
        {
            return @this switch
            {
                Protos.Statuses.Ok => Statuses.Ok,
                Protos.Statuses.BadRequest => Statuses.BadRequest,
                Protos.Statuses.Timeout => Statuses.Timeout,
                Protos.Statuses.NotFound => Statuses.NotFound,
                Protos.Statuses.Unauthorized => Statuses.Unauthorized,
                Protos.Statuses.NotImplemented => Statuses.NotImplemented,
                Protos.Statuses.InternalError => Statuses.InternalError,
                Protos.Statuses.Forbidden => Statuses.Forbidden,
                Protos.Statuses.TooManyRequests => Statuses.TooManyRequests,
                Protos.Statuses.Unavailable => Statuses.Unavailable,
                _ => Statuses.InternalError,
            };
        }

        /// <summary>The canonical gRPC status code of this status.</summary>
        public static Grpc.Core.StatusCode ToGrpcStatusCode(this Statuses @this)
        {
            return @this switch
            {
                Statuses.Ok => Grpc.Core.StatusCode.OK,
                Statuses.BadRequest => Grpc.Core.StatusCode.InvalidArgument,
                Statuses.Timeout => Grpc.Core.StatusCode.DeadlineExceeded,
                Statuses.NotFound => Grpc.Core.StatusCode.NotFound,
                Statuses.Unauthorized => Grpc.Core.StatusCode.Unauthenticated,
                Statuses.NotImplemented => Grpc.Core.StatusCode.Unimplemented,
                Statuses.InternalError => Grpc.Core.StatusCode.Internal,
                Statuses.Forbidden => Grpc.Core.StatusCode.PermissionDenied,
                Statuses.TooManyRequests => Grpc.Core.StatusCode.ResourceExhausted,
                Statuses.Unavailable => Grpc.Core.StatusCode.Unavailable,
                _ => Grpc.Core.StatusCode.Internal,
            };
        }

        public static Statuses FromGrpc(this Grpc.Core.StatusCode @this)
        {
            switch (@this)
            {
                case Grpc.Core.StatusCode.OK:
                    return Statuses.Ok;

                case Grpc.Core.StatusCode.InvalidArgument:
                case Grpc.Core.StatusCode.AlreadyExists:
                case Grpc.Core.StatusCode.FailedPrecondition:
                case Grpc.Core.StatusCode.OutOfRange:
                    return Statuses.BadRequest;

                case Grpc.Core.StatusCode.DeadlineExceeded:
                case Grpc.Core.StatusCode.Cancelled:
                    return Statuses.Timeout;

                case Grpc.Core.StatusCode.NotFound:
                    return Statuses.NotFound;

                case Grpc.Core.StatusCode.Unauthenticated:
                    return Statuses.Unauthorized;

                // No longer folded into Unauthorized: a caller that is merely not allowed gains
                // nothing from acquiring a new token, and a caller that is not authenticated gains
                // everything from it.
                case Grpc.Core.StatusCode.PermissionDenied:
                    return Statuses.Forbidden;

                case Grpc.Core.StatusCode.Unimplemented:
                    return Statuses.NotImplemented;

                // Retryable, and used to collapse into InternalError, which a caller must not retry
                case Grpc.Core.StatusCode.ResourceExhausted:
                    return Statuses.TooManyRequests;

                case Grpc.Core.StatusCode.Unavailable:
                    return Statuses.Unavailable;

                // Aborted is a concurrency conflict, so the request itself was well formed
                case Grpc.Core.StatusCode.Aborted:
                    return Statuses.BadRequest;

                case Grpc.Core.StatusCode.DataLoss:
                case Grpc.Core.StatusCode.Internal:
                case Grpc.Core.StatusCode.Unknown:
                default:
                    return Statuses.InternalError;
            }
        }

        public static int ToHttp(this Statuses @this)
        {
            return @this switch
            {
                Statuses.Ok => StatusCodes.Status200OK,
                Statuses.BadRequest => StatusCodes.Status400BadRequest,
                // 504, not 408: 408 says the CLIENT was too slow to send its request, while this
                // status means the work behind the call ran out of time. The proto always said 504.
                Statuses.Timeout => StatusCodes.Status504GatewayTimeout,
                Statuses.NotFound => StatusCodes.Status404NotFound,
                Statuses.Unauthorized => StatusCodes.Status401Unauthorized,
                Statuses.NotImplemented => StatusCodes.Status501NotImplemented,
                Statuses.InternalError => StatusCodes.Status500InternalServerError,
                Statuses.Forbidden => StatusCodes.Status403Forbidden,
                Statuses.TooManyRequests => StatusCodes.Status429TooManyRequests,
                Statuses.Unavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status500InternalServerError,
            };
        }

        public static Statuses FromHttp(this HttpStatusCode @this)
        {
            switch (@this)
            {
                case HttpStatusCode.OK:
                case HttpStatusCode.Created:
                case HttpStatusCode.Accepted:
                case HttpStatusCode.NoContent:
                    return Statuses.Ok;

                case HttpStatusCode.BadRequest:
                case HttpStatusCode.MethodNotAllowed:
                case HttpStatusCode.Conflict:
                case HttpStatusCode.LengthRequired:
                case HttpStatusCode.PreconditionFailed:
                case HttpStatusCode.RequestEntityTooLarge:
                case HttpStatusCode.RequestUriTooLong:
                case HttpStatusCode.UnsupportedMediaType:
                case HttpStatusCode.UnprocessableEntity:
                case HttpStatusCode.HttpVersionNotSupported:
                    return Statuses.BadRequest;

                case HttpStatusCode.RequestTimeout:
                case HttpStatusCode.GatewayTimeout:
                    return Statuses.Timeout;

                case HttpStatusCode.NotFound:
                case HttpStatusCode.Gone:
                    return Statuses.NotFound;

                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.ProxyAuthenticationRequired:
                    return Statuses.Unauthorized;

                case HttpStatusCode.Forbidden:
                case HttpStatusCode.PaymentRequired:
                    return Statuses.Forbidden;

                case HttpStatusCode.TooManyRequests:
                    return Statuses.TooManyRequests;

                case HttpStatusCode.NotImplemented:
                    return Statuses.NotImplemented;

                // 502 used to land on NotFound, which told the caller the resource does not exist
                // when in truth the service could not be reached - and one of those is worth
                // retrying while the other never is.
                case HttpStatusCode.BadGateway:
                case HttpStatusCode.ServiceUnavailable:
                    return Statuses.Unavailable;

                case HttpStatusCode.InternalServerError:
                default:
                    return Statuses.InternalError;
            }
        }

        /// <summary>
        /// Whether the same call is worth making again. InternalError is deliberately NOT retryable:
        /// it covers unknown faults, and repeating a call that may already have had an effect is
        /// worse than reporting the failure.
        /// </summary>
        public static bool IsRetryable(this Statuses @this)
        {
            return @this == Statuses.Timeout
                || @this == Statuses.TooManyRequests
                || @this == Statuses.Unavailable;
        }
    }
}
