namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// A fact an aggregate root wrote down, together with the scope it is ordered in.
    /// </summary>
    public readonly struct RecordedEvent
    {
        public RecordedEvent(IDomainEvent @event, string partitionKey)
        {
            Event = @event;
            PartitionKey = partitionKey;
        }

        public IDomainEvent Event { get; }
        public string PartitionKey { get; }
    }

    /// <summary>
    /// An aggregate root that records facts.
    ///
    /// The root keeps them itself rather than being handed a recorder, and that is deliberate: an
    /// aggregate is the guardian of an invariant, not a participant in the application's dependency
    /// graph. Giving it a service to call would make it need a container to exist, and a domain
    /// object that cannot be constructed in a test is a domain object nobody tests.
    ///
    /// So the root writes facts into itself, the repository drains them inside the save, and the
    /// unit of work that persisted the state is the one that persisted the intent to publish.
    /// </summary>
    public interface IEventRecordingRoot
    {
        /// <summary>
        /// Takes the recorded facts and forgets them. Called by the repository inside the save - a
        /// root that was loaded, changed and then NOT saved must not leave facts behind for the
        /// next save to pick up.
        /// </summary>
        IReadOnlyList<RecordedEvent> DrainRecordedEvents();
    }

    public static class EventRecordingExtensions
    {
        /// <summary>
        /// Moves everything the root recorded into the unit of work's recorder. What the repository
        /// calls, right before it drains the recorder into the outbox.
        /// </summary>
        public static void RecordAll(this IEventRecorder recorder, IEventRecordingRoot root)
        {
            if (root == null)
                return;

            foreach (var recorded in root.DrainRecordedEvents())
                recorder.Record(recorded.Event, recorded.PartitionKey);
        }
    }
}
