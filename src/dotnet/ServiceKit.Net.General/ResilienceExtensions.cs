using System.Net;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;

namespace ServiceKit.Net
{
    /// <summary>
    /// What a call between two services does when the other end is briefly not there.
    ///
    /// The answer used to be "fail", which turns a pod restart into an outage for everyone talking
    /// to it. The answer here is: retry the calls that CAN be retried, give up quickly on the ones
    /// that cannot, and put a deadline on everything so a stuck dependency cannot hold this service's
    /// threads for as long as it likes.
    /// </summary>
    public static class ResilienceExtensions
    {
        public class Options
        {
            /// <summary>Attempts in total, not retries after the first. 1 means "do not retry".</summary>
            public int MaximumAttempts = 3;

            /// <summary>The ceiling on one attempt.</summary>
            public TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

            /// <summary>The ceiling on the whole call, retries included.</summary>
            public TimeSpan TotalTimeout = TimeSpan.FromSeconds(30);

            public TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

            /// <summary>
            /// Whether a POST may be retried.
            ///
            /// Off, and that is the important default. A retried GET is free; a retried POST can
            /// place the order twice. Turn it on only for an endpoint that is idempotent in fact -
            /// because it takes an idempotency key, or because writing the same thing again is
            /// harmless.
            /// </summary>
            public bool RetryUnsafeMethods = false;
        }

        private static readonly HashSet<HttpStatusCode> _worthRetrying = new HashSet<HttpStatusCode>()
        {
            HttpStatusCode.RequestTimeout,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.BadGateway,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.GatewayTimeout,
        };

        /// <summary>
        /// Adds the house resilience pipeline to a named HttpClient.
        /// </summary>
        public static IHttpClientBuilder AddServiceKitResilience(this IHttpClientBuilder builder, Action<Options> configure = null)
        {
            var options = new Options();
            configure?.Invoke(options);

            builder.AddResilienceHandler("servicekit", pipeline =>
            {
                // Outermost: the promise made to the caller. Whatever happens inside - attempts,
                // waits, a slow server - the call ends by then.
                pipeline.AddTimeout(options.TotalTimeout);

                // "Do not retry" is the absence of the strategy, not a strategy configured to zero:
                // Polly rejects zero attempts as a mistake, and it usually is one.
                if (options.MaximumAttempts > 1)
                {
                    pipeline.AddRetry(new Polly.Retry.RetryStrategyOptions<HttpResponseMessage>()
                    {
                        MaxRetryAttempts = options.MaximumAttempts - 1,
                        Delay = options.RetryDelay,
                        // Backoff with jitter: without it, everything that failed together retries
                        // together, and the recovering service is knocked over by the herd it just
                        // dropped.
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        ShouldHandle = arguments => ValueTask.FromResult(_IsWorthRetrying(arguments.Outcome, options)),
                    });
                }

                // Innermost: the ceiling on ONE attempt, so a hung connection is abandoned and
                // retried rather than eating the whole budget.
                pipeline.AddTimeout(options.AttemptTimeout);
            });

            return builder;
        }

        private static bool _IsWorthRetrying(Outcome<HttpResponseMessage> outcome, Options options)
        {
            var request = outcome.Result?.RequestMessage;
            if (request != null && _IsSafeToRepeat(request.Method) == false && options.RetryUnsafeMethods == false)
                return false;

            if (outcome.Exception is HttpRequestException || outcome.Exception is TimeoutException)
                return true;

            return outcome.Result != null && _worthRetrying.Contains(outcome.Result.StatusCode);
        }

        // The methods HTTP defines as idempotent. POST and PATCH are not among them, and no amount
        // of convenience makes them so.
        private static bool _IsSafeToRepeat(HttpMethod method)
        {
            return method == HttpMethod.Get
                || method == HttpMethod.Head
                || method == HttpMethod.Options
                || method == HttpMethod.Put
                || method == HttpMethod.Delete
                || method == HttpMethod.Trace;
        }

        /// <summary>
        /// The same idea for gRPC, where it is the channel that carries it: a retry policy the
        /// server's own status codes drive, and a deadline on every call.
        ///
        /// gRPC retries are safer than HTTP ones by construction - the policy only fires on the
        /// status codes below, and a call that reached the handler does not report them.
        /// </summary>
        public static GrpcChannelOptions ServiceKitChannelOptions(Action<Options> configure = null)
        {
            var options = new Options();
            configure?.Invoke(options);

            var retry = new RetryPolicy()
            {
                MaxAttempts = Math.Max(1, options.MaximumAttempts),
                InitialBackoff = options.RetryDelay,
                MaxBackoff = TimeSpan.FromSeconds(5),
                BackoffMultiplier = 2,
            };

            // Unavailable is "nobody answered", DeadlineExceeded is "not in time", ResourceExhausted
            // is "not now". None of them says the call was carried out.
            retry.RetryableStatusCodes.Add(Grpc.Core.StatusCode.Unavailable);
            retry.RetryableStatusCodes.Add(Grpc.Core.StatusCode.DeadlineExceeded);
            retry.RetryableStatusCodes.Add(Grpc.Core.StatusCode.ResourceExhausted);

            return new GrpcChannelOptions()
            {
                ServiceConfig = new ServiceConfig()
                {
                    MethodConfigs = { new MethodConfig() { Names = { MethodName.Default }, RetryPolicy = retry } },
                },
            };
        }

        /// <summary>
        /// The deadline to put on a gRPC call that does not carry one of its own. A call without a
        /// deadline waits forever, which is how one stuck dependency takes a whole service with it.
        /// </summary>
        public static DateTime DefaultDeadline(Action<Options> configure = null)
        {
            var options = new Options();
            configure?.Invoke(options);

            return DateTime.UtcNow.Add(options.TotalTimeout);
        }
    }
}
