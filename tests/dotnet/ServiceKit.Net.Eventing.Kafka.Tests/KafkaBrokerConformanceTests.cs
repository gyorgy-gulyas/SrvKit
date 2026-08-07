using Microsoft.Extensions.Logging.Abstractions;
using ServiceKit.Net.Eventing.Kafka;
using ServiceKit.Net.Eventing.TestKit;
using Testcontainers.Kafka;

namespace ServiceKit.Net.Eventing.Kafka.Tests
{
    /// <summary>
    /// The Kafka adapter against the same contract the in-memory one meets.
    ///
    /// "It works with the in-memory broker" is not evidence about Kafka, which is the entire reason
    /// the conformance suite is a separate library rather than a set of tests next to one
    /// implementation.
    ///
    /// This class needs Docker. It is in its own project so that the SrvKit core suites keep the
    /// property they have always had - no container to run them - and so that a developer without
    /// Docker still gets a green build of everything else.
    /// </summary>
    [TestClass]
    public class KafkaBrokerConformanceTests : BrokerConformanceTests
    {
        private static KafkaContainer _kafka;
        private readonly List<KafkaEventBroker> _brokers = new();
        private int _channel;

        [ClassInitialize]
        public static async Task StartKafka(TestContext context)
        {
            _kafka = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();
            await _kafka.StartAsync();
        }

        [ClassCleanup]
        public static async Task StopKafka()
        {
            if (_kafka != null)
                await _kafka.DisposeAsync();
        }

        [TestCleanup]
        public void CloseBrokers()
        {
            foreach (var broker in _brokers)
                broker.Dispose();
            _brokers.Clear();
        }

        protected override IEventBroker CreateBroker()
        {
            var broker = new KafkaEventBroker(
                new KafkaEventBrokerOptions()
                {
                    BootstrapServers = _kafka.GetBootstrapAddress(),
                    // A rebalance in a short test is pure waiting; a single consumer per group does
                    // not need the default patience.
                    ConfigureConsumer = config =>
                    {
                        config.SessionTimeoutMs = 6000;
                        config.AllowAutoCreateTopics = true;
                    },
                    RetryBackoff = TimeSpan.FromMilliseconds(50),
                    MaxRetryBackoff = TimeSpan.FromMilliseconds(200),
                },
                NullLogger<KafkaEventBroker>.Instance);

            _brokers.Add(broker);
            return broker;
        }

        // A fresh channel per test: a real broker remembers, and two tests sharing a topic would
        // read each other's leftovers.
        protected override string NewChannel() => $"conformance.{Guid.NewGuid():N}.{Interlocked.Increment(ref _channel)}";

        // A container, a topic to auto-create and a group to join: minutes, not milliseconds.
        protected override TimeSpan DeliveryTimeout => TimeSpan.FromSeconds(60);
    }
}
