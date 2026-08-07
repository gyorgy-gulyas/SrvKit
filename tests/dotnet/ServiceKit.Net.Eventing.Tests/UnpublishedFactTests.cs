using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ServiceKit.Net.Eventing.Tests
{
    /// <summary>
    /// A fact that was recorded and never sent is silent data loss: the state was saved, the caller
    /// got a success, and nobody was told. The silence is the actual problem, so the end of a unit
    /// of work is where it has to stop being silent.
    /// </summary>
    [TestClass]
    public class UnpublishedFactTests
    {
        private sealed class CapturingLogger : ILogger<EventRecorder>
        {
            public readonly List<string> Errors = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (logLevel == LogLevel.Error)
                    Errors.Add(formatter(state, exception));
            }
        }

        private static EventRecorder NewRecorder(CapturingLogger logger, bool shouldThrow)
            => new EventRecorder(
                new JsonEventSerializer(),
                new EventRecordingContext(),
                Options.Create(new EventingOptions() { ThrowOnUnpublishedFacts = shouldThrow }),
                logger);

        [TestMethod]
        public void A_drained_recorder_says_nothing()
        {
            // The normal case has to stay quiet, or the alarm becomes noise and gets ignored.
            var logger = new CapturingLogger();
            var recorder = NewRecorder(logger, shouldThrow: true);

            recorder.Record(new OrderPlaced_v1() { orderId = "O-1" }, "O-1");
            recorder.Drain();
            recorder.Dispose();

            Assert.AreEqual(0, logger.Errors.Count);
        }

        [TestMethod]
        public void A_unit_of_work_that_never_recorded_anything_says_nothing()
        {
            var logger = new CapturingLogger();
            NewRecorder(logger, shouldThrow: true).Dispose();

            Assert.AreEqual(0, logger.Errors.Count);
        }

        [TestMethod]
        public void A_fact_that_never_reached_the_outbox_is_reported()
        {
            var logger = new CapturingLogger();
            var recorder = NewRecorder(logger, shouldThrow: false);

            recorder.Record(new OrderPlaced_v1() { orderId = "O-2" }, "O-2");
            recorder.Dispose();   // nobody ever appended it

            Assert.AreEqual(1, logger.Errors.Count);
            StringAssert.Contains(logger.Errors[0], "never reached the outbox");
            StringAssert.Contains(logger.Errors[0], "WebShop.Sales.Order.OrderPlaced.v1");
        }

        [TestMethod]
        public void In_development_and_tests_it_stops_the_line()
        {
            // Off by default in production: this runs during scope disposal, where an exception can
            // mask the real one and fail a request whose work already succeeded. In a test, failing
            // loudly is exactly what is wanted.
            var recorder = NewRecorder(new CapturingLogger(), shouldThrow: true);
            recorder.Record(new OrderPlaced_v1() { orderId = "O-3" }, "O-3");

            var failure = Assert.ThrowsException<InvalidOperationException>(() => recorder.Dispose());
            StringAssert.Contains(failure.Message, "never reached the outbox");
        }

        [TestMethod]
        public void It_reports_once_and_does_not_keep_shouting()
        {
            var logger = new CapturingLogger();
            var recorder = NewRecorder(logger, shouldThrow: false);

            recorder.Record(new OrderPlaced_v1() { orderId = "O-4" }, "O-4");
            recorder.Dispose();
            recorder.Dispose();

            Assert.AreEqual(1, logger.Errors.Count);
        }

        [TestMethod]
        public void It_names_every_kind_of_fact_that_was_lost()
        {
            var logger = new CapturingLogger();
            var recorder = NewRecorder(logger, shouldThrow: false);

            recorder.Record(new OrderPlaced_v1() { orderId = "O-5" }, "O-5");
            recorder.Record(new OrderCancelled() { orderId = "O-5" }, "O-5");
            recorder.Dispose();

            StringAssert.Contains(logger.Errors[0], "OrderPlaced.v1");
            StringAssert.Contains(logger.Errors[0], "OrderCancelled");
        }
    }
}
