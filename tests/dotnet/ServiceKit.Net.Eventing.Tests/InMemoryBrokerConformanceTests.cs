using ServiceKit.Net.Eventing.InMemory;
using ServiceKit.Net.Eventing.TestKit;

namespace ServiceKit.Net.Eventing.Tests
{
    /// <summary>
    /// The in-memory broker against the same contract every other adapter has to meet.
    ///
    /// This is what makes it a first-class implementation rather than a test double: the contract
    /// test that runs here takes the same path production takes, minus the network.
    /// </summary>
    [TestClass]
    public class InMemoryBrokerConformanceTests : BrokerConformanceTests
    {
        private int _channel;

        protected override IEventBroker CreateBroker() => new InMemoryEventBroker();

        protected override string NewChannel() => $"WebShop.Sales.{Interlocked.Increment(ref _channel)}";

        // Everything happens on the calling thread, so a delivery that has not happened by now is
        // not going to.
        protected override TimeSpan DeliveryTimeout => TimeSpan.FromSeconds(2);
    }
}
