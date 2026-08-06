namespace ServiceKit.Net.Communicators
{
    /// <summary>
    /// The bell in the application's own header.
    ///
    /// This is the one channel that does not leave the product, and that is exactly why the platform
    /// only owns the CONTRACT here: where an unread notification lives, how long it is kept and what
    /// counts as read are the product's decisions, not the platform's. A service asks for one the
    /// same way it asks for an e-mail, and the application decides what that means.
    /// </summary>
    public interface IInnerNotificationCommunicator
    {
        public class Notification
        {
            /// <summary>Whose bell rings. An identity id, as the calling context carries it.</summary>
            public string RecipientIdentityId;

            public string Title;

            public string Body;

            /// <summary>Where clicking it should go - a route in the application, not a URL.</summary>
            public string Link;

            /// <summary>
            /// Free-form, so a product can group or filter without the platform inventing a taxonomy
            /// for it.
            /// </summary>
            public string Category;
        }

        public Task<Response> Notify(Notification notification);

        /// <summary>What this recipient has not read yet.</summary>
        public Task<Response<IReadOnlyList<Notification>>> Unread(string recipientIdentityId);

        /// <summary>Everything this recipient has now seen.</summary>
        public Task<Response> MarkAllRead(string recipientIdentityId);
    }
}
