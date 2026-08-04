namespace ServiceKit.Net
{
    // The answer of every service operation: one status, and as many errors as there are things
    // wrong. The status sits here rather than on the errors because it describes the ANSWER - HTTP
    // and gRPC can each carry exactly one - while the errors describe what the caller has to fix.
    public class Response
    {
        public Statuses Status { get; protected set; } = Statuses.Ok;

        public IList<Error> Errors { get; protected set; } = new List<Error>();

        public Response()
        {
        }

        public Response(Statuses status, params Error[] errors)
        {
            Status = status;
            if (errors != null)
            {
                foreach (var error in errors)
                    Errors.Add(error);
            }
        }

        public Response(Statuses status, IEnumerable<Error> errors)
        {
            Status = status;
            if (errors != null)
            {
                foreach (var error in errors)
                    Errors.Add(error);
            }
        }

        public Response(Statuses status, string messageText, string additionalInformation = null)
            : this(status, new Error() { MessageText = messageText, AdditionalInformation = additionalInformation })
        {
        }

        // Carries a failure across response types unchanged, so a caller can hand one on without
        // rebuilding it - and without accidentally losing the errors on the way.
        public Response(Response failed)
            : this(failed.Status, failed.Errors)
        {
        }

        public bool IsSuccess() => Status == Statuses.Ok;
        public bool IsFailed() => Status != Statuses.Ok;

        public static Response Success() => new();
        public static Response<TValue> Success<TValue>(TValue value) => Response<TValue>.Success(value);
        public static Response Failure(Statuses status, params Error[] errors) => new(status, errors);
        public static Response Failure(Statuses status, string messageText, string additionalInformation = null) => new(status, messageText, additionalInformation);

        public Task<Response> AsTask() => Task.FromResult(this);
    }


    public class Response<TValue> : Response
    {
        public Response(TValue value)
        {
            Value = value;
        }

        public Response(Statuses status, params Error[] errors)
            : base(status, errors)
        {
            Value = default;
        }

        public Response(Statuses status, IEnumerable<Error> errors)
            : base(status, errors)
        {
            Value = default;
        }

        public Response(Statuses status, string messageText, string additionalInformation = null)
            : base(status, messageText, additionalInformation)
        {
            Value = default;
        }

        public Response(Response failed)
            : base(failed)
        {
            Value = default;
        }

        public TValue Value { get; private set; } = default;
        public bool HasValue() => Value != null;

        public static Response<TValue> Success(TValue value) => new(value);
        public static new Response<TValue> Failure(Statuses status, params Error[] errors) => new(status, errors);
        public static new Response<TValue> Failure(Statuses status, string messageText, string additionalInformation = null) => new(status, messageText, additionalInformation);

        public new Task<Response<TValue>> AsTask() => Task.FromResult(this);
    }
}
