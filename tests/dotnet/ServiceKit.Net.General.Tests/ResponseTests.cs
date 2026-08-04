namespace ServiceKit.Net.Tests
{
    [TestClass]
    public class ResponseTests
    {
        [TestMethod]
        public void A_successful_response_carries_no_errors()
        {
            var response = Response.Success();

            Assert.IsTrue(response.IsSuccess());
            Assert.AreEqual(Statuses.Ok, response.Status);
            Assert.AreEqual(0, response.Errors.Count);
        }

        [TestMethod]
        public void A_value_response_is_successful()
        {
            var response = Response.Success("the value");

            Assert.IsTrue(response.IsSuccess());
            Assert.IsTrue(response.HasValue());
            Assert.AreEqual("the value", response.Value);
        }

        [TestMethod]
        public void The_status_describes_the_answer_and_the_errors_describe_what_to_fix()
        {
            // A form with three bad fields is the ordinary case, not the exception: one status,
            // three errors, each pointing at its own control.
            var response = Response<string>.Failure(
                Statuses.BadRequest,
                new Error() { Path = "items[0].quantity", MessageText = "quantity must satisfy: value > 0" },
                new Error() { Path = "items[2].unitPrice", MessageText = "unitPrice must satisfy: value >= 0" },
                new Error() { Path = "billingAddress.country", MessageText = "country must satisfy: len(value) == 2" });

            Assert.IsTrue(response.IsFailed());
            Assert.AreEqual(Statuses.BadRequest, response.Status);
            Assert.AreEqual(3, response.Errors.Count);
            CollectionAssert.AreEqual(
                new[] { "items[0].quantity", "items[2].unitPrice", "billingAddress.country" },
                response.Errors.Select(error => error.Path).ToList());
        }

        [TestMethod]
        public void An_error_that_is_not_about_a_field_has_no_path()
        {
            var response = Response<string>.Failure(Statuses.NotFound, "Order 'x' does not exist");

            Assert.AreEqual(Statuses.NotFound, response.Status);
            Assert.AreEqual(1, response.Errors.Count);
            Assert.AreEqual(string.Empty, response.Errors[0].Path);
            Assert.AreEqual("Order 'x' does not exist", response.Errors[0].MessageText);
        }

        [TestMethod]
        public void A_failure_can_be_handed_on_without_losing_anything()
        {
            // A caller that only forwards a failure must not have to rebuild it - that is how
            // errors quietly get dropped on the way out.
            var inner = Response<int>.Failure(
                Statuses.Forbidden,
                new Error() { Path = "items[1].quantity", MessageText = "not allowed" });

            var forwarded = new Response<string>(inner);

            Assert.AreEqual(Statuses.Forbidden, forwarded.Status);
            Assert.AreEqual(1, forwarded.Errors.Count);
            Assert.AreEqual("items[1].quantity", forwarded.Errors[0].Path);
            Assert.IsFalse(forwarded.HasValue());
        }

        [TestMethod]
        public void A_failed_value_response_carries_no_value()
        {
            var response = Response<string>.Failure(Statuses.BadRequest, "nope");

            Assert.IsTrue(response.IsFailed());
            Assert.IsFalse(response.HasValue());
            Assert.IsNull(response.Value);
        }

        [TestMethod]
        public void The_status_of_a_failure_is_what_reaches_the_transport()
        {
            // There is exactly one of these because HTTP and gRPC can each carry exactly one -
            // which is the whole reason it sits on the response rather than on the errors.
            var response = Response.Failure(Statuses.Unavailable, "the warehouse is down");

            Assert.AreEqual(503, response.Status.ToHttp());
            Assert.AreEqual(Grpc.Core.StatusCode.Unavailable, response.Status.ToGrpcStatusCode());
            Assert.IsTrue(response.Status.IsRetryable());
        }
    }
}
