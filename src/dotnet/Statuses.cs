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
}