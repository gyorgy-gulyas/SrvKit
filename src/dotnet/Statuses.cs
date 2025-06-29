using Microsoft.AspNetCore.Http;
using System.Net;

namespace ServiceKit.Net
{
    public enum Statuses
    {
        /// gRPC: Ok(0), Http: 200 (Ok)
        Ok, 
        /// gRPC: INVALID_ARGUMENT(3), Http: 400 (BadRequest)
        BadRequest,
        /// gRPC: DEADLINE_EXCEEDED (4), Http: 408 (Request Timeout)
        Timeout,
        /// gRPC: NOT_FOUND (5), Http: 404 (404 Not Found)
        NotFound,
        /// gRPC: UNAUTHENTICATED (16), Http: 401 (Unauthorized)
        Unauthorized,
        /// gRPC: UNIMPLEMENTED (12), Http: 501 (Not Implemented)
        NotImplemented,
        /// gRPC: INTERNAL (13), Http: 500 (Internal Server Error)
        InternalError,
    }

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
                _ => Statuses.InternalError,
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

                case Grpc.Core.StatusCode.Cancelled:
                case Grpc.Core.StatusCode.DeadlineExceeded:
                case Grpc.Core.StatusCode.Aborted:
                    return Statuses.Timeout;


                case Grpc.Core.StatusCode.NotFound:
                    return Statuses.NotFound;

                case Grpc.Core.StatusCode.PermissionDenied:
                case Grpc.Core.StatusCode.Unauthenticated:
                    return Statuses.Unauthorized;

                case Grpc.Core.StatusCode.Unimplemented:
                    return Statuses.NotImplemented;

                case Grpc.Core.StatusCode.DataLoss:
                case Grpc.Core.StatusCode.Unavailable:
                case Grpc.Core.StatusCode.Internal:
                case Grpc.Core.StatusCode.Unknown:
                case Grpc.Core.StatusCode.ResourceExhausted:
                default:
                    return Statuses.InternalError;
            }
            ;
        }

        public static int ToHttp(this Statuses @this)
        {
            return @this switch
            {
                Statuses.Ok => StatusCodes.Status200OK,
                Statuses.BadRequest => StatusCodes.Status400BadRequest,
                Statuses.Timeout => StatusCodes.Status408RequestTimeout,
                Statuses.NotFound => StatusCodes.Status404NotFound,
                Statuses.Unauthorized => StatusCodes.Status401Unauthorized,
                Statuses.NotImplemented => StatusCodes.Status501NotImplemented,
                Statuses.InternalError => StatusCodes.Status500InternalServerError,
                _ => StatusCodes.Status500InternalServerError,
            };
        }

        public static Statuses FromHttp(this HttpStatusCode @this)
        {
            switch (@this)
            {
                case HttpStatusCode.OK:
                    return Statuses.Ok;

                case HttpStatusCode.BadRequest:
                case HttpStatusCode.Ambiguous:
                case HttpStatusCode.Moved:
                case HttpStatusCode.LengthRequired:
                case HttpStatusCode.PreconditionFailed:
                case HttpStatusCode.RequestEntityTooLarge:
                case HttpStatusCode.RequestUriTooLong:
                case HttpStatusCode.UnsupportedMediaType:
                case HttpStatusCode.HttpVersionNotSupported:
                    return Statuses.BadRequest;

                case HttpStatusCode.RequestTimeout:
                    return Statuses.Timeout;

                case HttpStatusCode.NotFound:
                case HttpStatusCode.BadGateway:
                    return Statuses.NotFound;

                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.PaymentRequired:
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.NonAuthoritativeInformation:
                case HttpStatusCode.MethodNotAllowed:
                case HttpStatusCode.NetworkAuthenticationRequired:
                case HttpStatusCode.ProxyAuthenticationRequired:
                    return Statuses.Unauthorized;

                case HttpStatusCode.NotImplemented:
                    return Statuses.NotImplemented;

                case HttpStatusCode.InternalServerError:
                    return Statuses.InternalError;

                default:
                    return Statuses.InternalError;
            }
        }
    }
}