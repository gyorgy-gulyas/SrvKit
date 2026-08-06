using Microsoft.AspNetCore.Http;
using System.Runtime.CompilerServices;
using System.Security.Claims;

namespace ServiceKit.Net.Tests
{
    [TestClass]
    public class GrpcRegistrationTests
    {
        [TestMethod]
        public void The_grpc_mapping_method_is_still_found_exactly_once()
        {
            // MapGrpcControllers reaches MapGrpcService by reflection, resolved in a static field
            // with Single(). If Grpc.AspNetCore ever adds another single-argument generic overload
            // the lookup becomes ambiguous - and this test is what says so, instead of the host
            // picking one at random at startup.
            RuntimeHelpers.RunClassConstructor(typeof(RegistrationExtensions).TypeHandle);
        }
    }

    [TestClass]
    public class CallingContextTests
    {
        private static HttpContext Request(
            string identityId = "user-1",
            string identityName = "Alice",
            string identityType = "User",
            string tenantId = "tenant-1",
            string correlationId = "corr-1",
            string callStack = "")
        {
            var http = new DefaultHttpContext();
            http.Request.Headers[ServiceConstans.const_identity_id] = identityId;
            http.Request.Headers[ServiceConstans.const_identity_name] = identityName;
            http.Request.Headers[ServiceConstans.const_identity_type] = identityType;
            http.Request.Headers[ServiceConstans.const_tenant_id] = tenantId;
            http.Request.Headers[ServiceConstans.const_correlation_id] = correlationId;
            http.Request.Headers[ServiceConstans.const_call_stack] = callStack;
            return http;
        }

        [TestMethod]
        public void The_context_is_read_from_the_request_headers()
        {
            var ctx = CallingContext.FromHttpContext(Request());

            Assert.AreEqual("user-1", ctx.IdentityId);
            Assert.AreEqual("Alice", ctx.IdentityName);
            Assert.AreEqual(CallingContext.IdentityTypes.User, ctx.IdentityType);
            Assert.AreEqual("tenant-1", ctx.TenantId);
            Assert.AreEqual("corr-1", ctx.CorrelationId);
        }

        [TestMethod]
        public void An_unknown_identity_type_does_not_become_a_service()
        {
            // Anything unparseable has to land on Unknown - IdentityType decides whether a system
            // only operation may run, so a wrong guess here is an authorization hole.
            var ctx = CallingContext.FromHttpContext(Request(identityType: "nonsense"));

            Assert.AreEqual(CallingContext.IdentityTypes.Unknown, ctx.IdentityType);
        }

        [TestMethod]
        public void Every_request_gets_its_own_context()
        {
            var first = CallingContext.FromHttpContext(Request(identityId: "user-1", identityName: "Alice"));
            var second = CallingContext.FromHttpContext(Request(identityId: "user-2", identityName: "Bob"));

            Assert.AreNotSame(first, second);
            Assert.AreEqual("user-1", first.IdentityId);
            Assert.AreEqual("user-2", second.IdentityId);
        }

        [TestMethod]
        public void Releasing_a_context_neither_clears_it_nor_recycles_it()
        {
            // Generated controllers call ReturnToPool in a finally block, while background work
            // started by the request - the audit trail, for one - still holds this instance and
            // reads the identity off it later. So the call must leave the context intact, and the
            // instance must never come back for another request.
            var finished = CallingContext.FromHttpContext(Request(identityId: "user-1", identityName: "Alice"));
            finished.ReturnToPool();

            Assert.AreEqual("user-1", finished.IdentityId);
            Assert.AreEqual("Alice", finished.IdentityName);

            var next = CallingContext.FromHttpContext(Request(identityId: "user-2", identityName: "Bob"));

            Assert.AreNotSame(finished, next);
            Assert.AreEqual("user-1", finished.IdentityId);
            Assert.AreEqual("user-2", next.IdentityId);
        }

        [TestMethod]
        public void Claims_come_from_the_authenticated_user()
        {
            var http = Request();
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("role", "admin"), new Claim("scope", "orders") },
                authenticationType: "Bearer"));

            var ctx = CallingContext.FromHttpContext(http);

            Assert.AreEqual("admin", ctx.Claims["role"]);
            Assert.AreEqual("orders", ctx.Claims["scope"]);
        }

        [TestMethod]
        public void An_anonymous_request_carries_no_claims()
        {
            var ctx = CallingContext.FromHttpContext(Request());

            Assert.AreEqual(0, ctx.Claims.Count);
        }

        [TestMethod]
        public void Claims_are_never_written_into_outgoing_http_headers()
        {
            var http = Request();
            http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("role", "admin") }, "Bearer"));
            var ctx = CallingContext.FromHttpContext(http);

            var outgoing = new HttpRequestMessage();
            ctx.FillHttpRequest(outgoing, "OrderService", "place");

            Assert.IsFalse(outgoing.Headers.Any(header => header.Key.StartsWith(ServiceConstans.const_claim)));
            Assert.IsFalse(outgoing.Headers.Any(header => header.Value.Any(value => value == "admin")));
        }

        [TestMethod]
        public void An_outgoing_call_carries_the_identity_and_extends_the_call_stack()
        {
            var ctx = CallingContext.FromHttpContext(Request(callStack: "Gateway.handle"));

            var outgoing = new HttpRequestMessage();
            ctx.FillHttpRequest(outgoing, "OrderService", "place");

            Assert.AreEqual("user-1", outgoing.Headers.GetValues(ServiceConstans.const_identity_id).Single());
            Assert.AreEqual("Gateway.handle -> OrderService.place", outgoing.Headers.GetValues(ServiceConstans.const_call_stack).Single());
        }

        [TestMethod]
        public void The_call_stack_starts_at_the_first_hop()
        {
            var ctx = CallingContext.FromHttpContext(Request(callStack: ""));

            var outgoing = new HttpRequestMessage();
            ctx.FillHttpRequest(outgoing, "OrderService", "place");

            Assert.AreEqual("OrderService.place", outgoing.Headers.GetValues(ServiceConstans.const_call_stack).Single());
        }

        [TestMethod]
        public void Grpc_metadata_carries_the_claims_but_http_headers_do_not()
        {
            var http = Request();
            http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("role", "admin") }, "Bearer"));
            var ctx = CallingContext.FromHttpContext(http);

            var metadata = ctx.ToGrpcMetadata("OrderService", "place");

            Assert.AreEqual("admin", metadata.GetValue(ServiceConstans.const_claim + "role"));
        }

        [TestMethod]
        public void Cloning_replaces_the_identity_and_keeps_the_rest()
        {
            var ctx = CallingContext.FromHttpContext(Request(callStack: "Gateway.handle"));

            var clone = ctx.CloneWithIdentity("service-1", "OrderService", CallingContext.IdentityTypes.Service);

            Assert.AreEqual("service-1", clone.IdentityId);
            Assert.AreEqual(CallingContext.IdentityTypes.Service, clone.IdentityType);
            Assert.AreEqual("tenant-1", clone.TenantId);
            Assert.AreEqual("corr-1", clone.CorrelationId);
            Assert.AreEqual("Gateway.handle", clone.CallStack);
            // the original must not move with it
            Assert.AreEqual("user-1", ctx.IdentityId);
        }

        [TestMethod]
        public void A_context_nobody_filled_in_is_not_a_user()
        {
            // IdentityTypes.User is the first value of the enum, so an unfilled context used to
            // claim to be one - which is the one answer an authorization check must never be given
            // for free.
            Assert.AreEqual(CallingContext.IdentityTypes.Unknown, new CallingContext().IdentityType);
        }

        [TestMethod]
        public void A_claim_is_found_by_the_name_it_was_sent_under_whatever_its_case()
        {
            // gRPC lowercases metadata keys and HTTP claim types keep their case, so the same claim
            // used to be found over one transport and missed over the other.
            var http = new DefaultHttpContext();
            http.Request.Headers[ServiceConstans.const_identity_id] = "user-1";
            http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Role", "admin") }, "test"));

            var ctx = CallingContext.FromHttpContext(http);

            Assert.AreEqual("admin", ctx.Claims["role"]);
            Assert.AreEqual("admin", ctx.Claims["ROLE"]);
        }

        [TestMethod]
        public void A_clone_carries_the_claims_and_does_not_share_them()
        {
            // The clone is how a service acts on someone's behalf. Dropping the claims there drops
            // the authorization context exactly where it matters most.
            var http = new DefaultHttpContext();
            http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("role", "admin") }, "test"));
            var ctx = CallingContext.FromHttpContext(http);

            var clone = ctx.CloneWithIdentity("service-1", "OrderService", CallingContext.IdentityTypes.Service);

            Assert.AreEqual("admin", clone.Claims["role"]);

            clone.Claims["role"] = "nobody";
            Assert.AreEqual("admin", ctx.Claims["role"], "the clone must not reach back into the request that spawned it");
        }
    }
}
