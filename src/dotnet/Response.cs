namespace ServiceKit.Net
{
    public class Response
    {
        public Error Error { get; private set; } = null;
        public bool IsSuccess() => Error == null;
        public static Response Success() => new();
        public static Response Failure(Error error) => new() { Error = error };
    }

    public class Response<TValue1> : Response
        where TValue1 : class
    {
        public TValue1 Value1 { get; private set; } = null;
        public bool HasValue1() => Value1 != null;
        public static Response<TValue1> Success(TValue1 value) => new() { Value1 = value };
    }

    public class Response<TValue1, TValue2> : Response<TValue1>
        where TValue1 : class
        where TValue2 : class
    {
        public TValue2 Value2 { get; private set; } = null;
        public bool HasValue2() => Value2 != null;
        public static Response<TValue1, TValue2> Success(TValue2 value) => new() { Value2 = value };
    }

    public class Response<TValue1, TValue2, TValue3> : Response<TValue1, TValue2>
        where TValue1 : class
        where TValue2 : class
        where TValue3 : class
    {
        public TValue3 Value3 { get; private set; } = null;
        public bool HasValue3() => Value3 != null;
        public static Response<TValue1, TValue2, TValue3> Success(TValue3 value) => new() { Value3 = value };
    }
    
    public class Response<TValue1, TValue2, TValue3, TValue4> : Response<TValue1,TValue2,TValue3>
        where TValue1 : class
        where TValue2 : class
        where TValue3 : class
        where TValue4 : class
    {
        public TValue4 Value4 { get; private set; } = null;
        public bool HasValue4() => Value4 != null;
        public static Response<TValue1, TValue2, TValue3, TValue4> Success(TValue4 value) => new() { Value4 = value };
    }
}