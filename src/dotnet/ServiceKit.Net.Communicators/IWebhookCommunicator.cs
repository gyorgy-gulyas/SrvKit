namespace ServiceKit.Net.Communicators
{
    /// <summary>
    /// Telling somebody else's system that something happened here.
    ///
    /// A webhook is the one channel whose recipient is not a person, which changes what matters
    /// about it: the receiver has to be able to tell that the call really came from us and that it
    /// has not already handled it. So a delivery carries a signature and an id, and neither is
    /// optional.
    /// </summary>
    public interface IWebhookCommunicator
    {
        public class Delivery
        {
            /// <summary>Where it goes. The subscriber's own address, so it is per call, not configured.</summary>
            public string Url;

            /// <summary>What happened - the receiver usually routes on this.</summary>
            public string EventType;

            /// <summary>The body. Serialised as JSON.</summary>
            public object Payload;

            /// <summary>
            /// Identifies THIS delivery. A receiver that has seen it before can drop it, which is
            /// what makes a retry safe. Left empty, one is generated.
            /// </summary>
            public string DeliveryId;
        }

        public Task<Response> Send(Delivery delivery);
    }
}
