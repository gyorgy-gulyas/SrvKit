namespace ServiceKit.Net.Communicators
{
    /// <summary>
    /// A notification on somebody's phone.
    ///
    /// The device tokens are the caller's business - they belong to the product's own user store -
    /// and the gateway that turns them into a delivery is deployment detail.
    /// </summary>
    public interface IPushCommunicator
    {
        public class Notification
        {
            /// <summary>The devices to reach. One person usually has several.</summary>
            public IEnumerable<string> DeviceTokens;

            public string Title;

            public string Body;

            /// <summary>
            /// Carried through to the application rather than shown. This is what lets a tap open
            /// the order it was about instead of the home screen.
            /// </summary>
            public IDictionary<string, string> Data;

            public bool HasRecipient()
            {
                return DeviceTokens != null && DeviceTokens.Any(token => string.IsNullOrWhiteSpace(token) == false);
            }
        }

        public Task<Response> Send(Notification notification);
    }
}
