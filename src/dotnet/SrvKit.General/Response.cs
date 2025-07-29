namespace ServiceKit.Net
{
    public class Response
    {
        public Error Error { get; protected set; } = null;

        public Response()
        {
            Error = null;
        }

        public Response(Error error)
        {
            Error = error;
        }

        public bool IsSuccess() => Error == null;
        public static Response Success() => new();
        public static Response Failure(Error error) => new() { Error = error };
    }

    public class Response<TValue> : Response
        where TValue : class
    {
        public Response(TValue value)
        {
            Value = value;
            Error = null;
        }

        public Response(Error error)
            : base(error)
        {
            Value = null;
        }

        public TValue Value { get; private set; } = null;
        public bool HasValue() => Value != null;
        public static Response<TValue> Success(TValue value) => new(value);
        public static new Response<TValue> Failure(Error error) => new(error);
    }
}