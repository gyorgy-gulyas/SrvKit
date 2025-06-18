namespace ServiceKit.Net
{
    public enum Statuses
    {
        /// gRPC: Ok(0), Http: 200 (Ok)
        Ok, 
        /// gRPC: INVALID_ARGUMENT(3), Http: 400 (BadRequest)
        BadRequest,
        /// gRPC: DEADLINE_EXCEEDED (4), Http: 504 (Gateway Timeout)
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

        public static Statuses ToDotnet(this Protos.Statuses @this)
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
    }
}