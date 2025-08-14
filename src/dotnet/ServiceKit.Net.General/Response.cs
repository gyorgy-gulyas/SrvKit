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
        public bool IsFailed() => Error != null;
        public static Response Success() => new();
        public static Response<TValue> Success<TValue>( TValue value ) => Response<TValue>.Success( value );
        public static Response Failure(Error error) => new() { Error = error };
        public Task<Response> AsTask() => Task.FromResult(this);
    }


    public class Response<TValue> : Response
    {
        public Response(TValue value)
        {
            Value = value;
            Error = null;
        }

        public Response(Error error)
            : base(error)
        {
            Value = default;
        }

        public TValue Value { get; private set; } = default;
        public bool HasValue() => Value != null;
        public static Response<TValue> Success(TValue value) => new(value);
        public static new Response<TValue> Failure(Error error) => new(error);
        public new Task<Response<TValue>> AsTask() => Task.FromResult(this);
    }
}