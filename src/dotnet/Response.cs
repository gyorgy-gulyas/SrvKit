namespace ServiceKit.Net
{
    public class Response
    {
        public Error Error { get; private set; } = null;
        public bool IsSuccess() => Error == null;
        public static Response Success() => new();
        public static Response Failure(Error error) => new() { Error = error };
    }

    public class Response<TValue> : Response
        where TValue : class
    {
        public TValue Value { get; private set; } = null;
        public bool HasValue() => Value != null;
        public static Response<TValue> Success(TValue value) => new() { Value = value };
    }
}