using System.Collections.Concurrent;

namespace ServiceKit.Net.Communicators.Implementations
{
    /// <summary>
    /// In-app notifications kept in this process.
    ///
    /// NOT for production, and the class name says so rather than a comment nobody reads: they are
    /// lost when the process restarts and invisible to the other replicas. It exists so that a
    /// service can be written and tested against the channel before the product has decided where
    /// unread notifications live - which is a product decision, not a platform one.
    ///
    /// Replace it with an implementation over the product's own store and nothing calling it
    /// changes.
    /// </summary>
    public class InMemoryInnerNotificationCommunicator : IInnerNotificationCommunicator
    {
        private readonly ConcurrentDictionary<string, List<IInnerNotificationCommunicator.Notification>> _unread =
            new ConcurrentDictionary<string, List<IInnerNotificationCommunicator.Notification>>(StringComparer.OrdinalIgnoreCase);

        Task<Response> IInnerNotificationCommunicator.Notify(IInnerNotificationCommunicator.Notification notification)
        {
            if (notification == null || string.IsNullOrWhiteSpace(notification.RecipientIdentityId) == true)
                return Response.Failure(Statuses.BadRequest, "The notification has no recipient").AsTask();

            var forRecipient = _unread.GetOrAdd(notification.RecipientIdentityId, _ => new List<IInnerNotificationCommunicator.Notification>());
            lock (forRecipient)
                forRecipient.Add(notification);

            return Response.Success().AsTask();
        }

        Task<Response<IReadOnlyList<IInnerNotificationCommunicator.Notification>>> IInnerNotificationCommunicator.Unread(string recipientIdentityId)
        {
            if (string.IsNullOrWhiteSpace(recipientIdentityId) == true)
                return Task.FromResult(new Response<IReadOnlyList<IInnerNotificationCommunicator.Notification>>(Statuses.BadRequest, "No recipient"));

            if (_unread.TryGetValue(recipientIdentityId, out var forRecipient) == false)
                return Task.FromResult(new Response<IReadOnlyList<IInnerNotificationCommunicator.Notification>>(Array.Empty<IInnerNotificationCommunicator.Notification>()));

            lock (forRecipient)
                return Task.FromResult(new Response<IReadOnlyList<IInnerNotificationCommunicator.Notification>>(forRecipient.ToList()));
        }

        Task<Response> IInnerNotificationCommunicator.MarkAllRead(string recipientIdentityId)
        {
            if (string.IsNullOrWhiteSpace(recipientIdentityId) == true)
                return Response.Failure(Statuses.BadRequest, "No recipient").AsTask();

            _unread.TryRemove(recipientIdentityId, out _);
            return Response.Success().AsTask();
        }
    }
}
