using Microsoft.AspNetCore.Http;
using System.Net;

namespace ServiceKit.Net.Tests
{
    [TestClass]
    public class StatusesTests
    {
        private static IEnumerable<Statuses> AllStatuses => Enum.GetValues<Statuses>();

        [TestMethod]
        public void A_status_survives_a_round_trip_through_http()
        {
            // This is what the whole mapping is for: a status that crosses a service boundary and
            // comes back has to be the same status. Without it a caller cannot tell what happened,
            // and cannot decide whether to try again.
            foreach (var status in AllStatuses)
            {
                var carried = ((HttpStatusCode)status.ToHttp()).FromHttp();
                Assert.AreEqual(status, carried, $"{status} did not survive the HTTP round trip");
            }
        }

        [TestMethod]
        public void A_status_survives_a_round_trip_through_grpc()
        {
            foreach (var status in AllStatuses)
            {
                var carried = status.ToGrpcStatusCode().FromGrpc();
                Assert.AreEqual(status, carried, $"{status} did not survive the gRPC round trip");
            }
        }

        [TestMethod]
        public void A_status_survives_a_round_trip_through_the_error_payload()
        {
            foreach (var status in AllStatuses)
            {
                Assert.AreEqual(status, status.ToGrpc().FromGrpc(), $"{status} did not survive the payload round trip");
            }
        }

        [TestMethod]
        public void The_csharp_enum_and_the_proto_enum_agree()
        {
            // They are two hand-kept lists of the same thing; a number that drifts apart would
            // silently turn one status into another on the wire.
            foreach (var status in AllStatuses)
            {
                var onTheWire = status.ToGrpc();
                Assert.AreEqual((int)status, (int)onTheWire, $"{status} has a different number in the proto");
                Assert.AreEqual(status.ToString(), onTheWire.ToString(), $"{status} has a different name in the proto");
            }
        }

        [TestMethod]
        public void A_timeout_is_a_gateway_timeout_not_a_request_timeout()
        {
            // 408 says the CLIENT was too slow to send its request; this status means the work
            // behind the call ran out of time. The proto always said 504.
            Assert.AreEqual(StatusCodes.Status504GatewayTimeout, Statuses.Timeout.ToHttp());
            Assert.AreEqual(Statuses.Timeout, HttpStatusCode.GatewayTimeout.FromHttp());
            Assert.AreEqual(Statuses.Timeout, HttpStatusCode.RequestTimeout.FromHttp());
        }

        [TestMethod]
        public void An_unreachable_service_is_not_a_missing_resource()
        {
            // 502 used to map to NotFound, telling the caller the resource does not exist when in
            // truth the service could not be reached - and one of those is worth retrying.
            Assert.AreEqual(Statuses.Unavailable, HttpStatusCode.BadGateway.FromHttp());
            Assert.AreEqual(Statuses.Unavailable, HttpStatusCode.ServiceUnavailable.FromHttp());
        }

        [TestMethod]
        public void Not_authenticated_and_not_allowed_are_different_answers()
        {
            // Acquiring a fresh token fixes one of them and never the other
            Assert.AreEqual(Statuses.Unauthorized, HttpStatusCode.Unauthorized.FromHttp());
            Assert.AreEqual(Statuses.Forbidden, HttpStatusCode.Forbidden.FromHttp());
            Assert.AreEqual(Statuses.Unauthorized, Grpc.Core.StatusCode.Unauthenticated.FromGrpc());
            Assert.AreEqual(Statuses.Forbidden, Grpc.Core.StatusCode.PermissionDenied.FromGrpc());
        }

        [TestMethod]
        public void A_busy_or_down_service_does_not_collapse_into_an_internal_error()
        {
            // Both used to become InternalError, which a caller must not retry
            Assert.AreEqual(Statuses.Unavailable, Grpc.Core.StatusCode.Unavailable.FromGrpc());
            Assert.AreEqual(Statuses.TooManyRequests, Grpc.Core.StatusCode.ResourceExhausted.FromGrpc());
        }

        [TestMethod]
        public void Only_the_transient_failures_are_retryable()
        {
            Assert.IsTrue(Statuses.Timeout.IsRetryable());
            Assert.IsTrue(Statuses.TooManyRequests.IsRetryable());
            Assert.IsTrue(Statuses.Unavailable.IsRetryable());

            // InternalError covers unknown faults: the call may already have had an effect, so
            // repeating it is worse than reporting the failure
            Assert.IsFalse(Statuses.InternalError.IsRetryable());
            Assert.IsFalse(Statuses.BadRequest.IsRetryable());
            Assert.IsFalse(Statuses.Unauthorized.IsRetryable());
            Assert.IsFalse(Statuses.Forbidden.IsRetryable());
            Assert.IsFalse(Statuses.NotFound.IsRetryable());
            Assert.IsFalse(Statuses.Ok.IsRetryable());
        }

        [TestMethod]
        public void An_unknown_grpc_code_is_an_internal_error_not_a_success()
        {
            Assert.AreEqual(Statuses.InternalError, Grpc.Core.StatusCode.Unknown.FromGrpc());
            Assert.AreEqual(Statuses.InternalError, Grpc.Core.StatusCode.DataLoss.FromGrpc());
            Assert.AreEqual(Statuses.InternalError, ((HttpStatusCode)599).FromHttp());
        }

        [TestMethod]
        public void Every_status_has_its_own_http_and_grpc_code()
        {
            // Two statuses sharing an outbound code would make the round trip above impossible
            var httpCodes = AllStatuses.Select(status => status.ToHttp()).ToList();
            var grpcCodes = AllStatuses.Select(status => status.ToGrpcStatusCode()).ToList();

            CollectionAssert.AllItemsAreUnique(httpCodes);
            CollectionAssert.AllItemsAreUnique(grpcCodes);
        }
    }
}
