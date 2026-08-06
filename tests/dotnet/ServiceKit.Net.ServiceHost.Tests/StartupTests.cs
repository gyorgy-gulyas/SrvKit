using Microsoft.Extensions.Hosting;

namespace ServiceKit.Net.Tests
{
    // _BeforeRun is where a host does its own startup work - a migration, a warm-up, a first fetch.
    // It returns a Task, and the factory used to block on it.
    [TestClass]
    public class StartupTests
    {
        [TestCleanup]
        public void Cleanup()
        {
            TestServiceHost.BeforeRunThrows = null;
        }

        [TestMethod]
        public async Task The_startup_hook_is_awaited()
        {
            var started = await TestServiceHost.Start();

            using (started.Host)
                await started.Host.StopAsync();
        }

        [TestMethod]
        public async Task A_failing_startup_reports_the_reason_and_not_a_wrapper()
        {
            // Wait() wraps whatever the hook threw in an AggregateException, so a host that failed
            // to start said "one or more errors occurred" and pointed its stack trace at the
            // factory instead of at the code that broke.
            TestServiceHost.BeforeRunThrows = new InvalidOperationException("the migration failed");

            var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => BaseServiceHost.CreateAsync<TestServiceHost>(
                    new[] { "--urls", "http://127.0.0.1:0", "--applicationName", "ServiceKit.Net.ServiceHost.Tests" },
                    TestServiceHost.DefaultOptions));

            Assert.AreEqual("the migration failed", thrown.Message);
        }

        [TestMethod]
        public void The_synchronous_entry_point_unwraps_the_failure_too()
        {
            // Program.cs written against Create should not have to change, and should not start
            // reporting wrappers either.
            TestServiceHost.BeforeRunThrows = new InvalidOperationException("the migration failed");

            var thrown = Assert.ThrowsException<InvalidOperationException>(
                () => BaseServiceHost.Create<TestServiceHost>(
                    new[] { "--urls", "http://127.0.0.1:0", "--applicationName", "ServiceKit.Net.ServiceHost.Tests" },
                    TestServiceHost.DefaultOptions));

            Assert.AreEqual("the migration failed", thrown.Message);
        }
    }
}
