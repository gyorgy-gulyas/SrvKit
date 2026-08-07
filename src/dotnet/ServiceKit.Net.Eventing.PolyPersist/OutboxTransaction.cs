using PolyPersist;

namespace ServiceKit.Net.Eventing.PolyPersistStores
{
    /// <summary>
    /// A unit of work that carries the facts out with the state.
    ///
    /// It is an <see cref="ITransaction"/>, so existing code keeps working unchanged; the only
    /// difference is how it was created. That is the whole idea: the thing a developer must not
    /// forget is attached to the object they cannot avoid using. Save an aggregate root that
    /// recorded facts, and the facts are queued into the outbox by the same call - not by a second
    /// one somebody has to remember.
    ///
    /// It does NOT require that a save produce facts. Plenty of saves legitimately produce none - a
    /// corrected typo, an idempotent re-save - and demanding a fact would only teach people to
    /// invent one. The invariant runs the other way: an empty outbox is fine, a recorded fact that
    /// never reached the outbox is a bug.
    /// </summary>
    public sealed class OutboxTransaction : ITransaction
    {
        private readonly ITransaction _inner;
        private readonly IOutboxStore _outbox;
        private readonly IEventRecorder _recorder;

        public OutboxTransaction(ITransaction inner, IOutboxStore outbox, IEventRecorder recorder)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        }

        /// <summary>How many facts this unit of work has queued so far. For tests and diagnostics.</summary>
        public int QueuedFactCount { get; private set; }

        /// <summary>
        /// Queues a fact that has no aggregate to belong to - a context-level fact, recorded by a
        /// service rather than by a root. The automatic path below covers everything that DOES have
        /// a root, which is the common case.
        /// </summary>
        public async Task Record(IDomainEvent @event, string partitionKey)
        {
            _recorder.Record(@event, partitionKey);
            await Flush();
        }

        /// <summary>
        /// Takes whatever the saved object recorded and queues it into the outbox, inside this same
        /// unit of work.
        /// </summary>
        private async Task DrainIfRecordingRoot(object saved)
        {
            if (saved is not IEventRecordingRoot root)
                return;

            _recorder.RecordAll(root);
            await Flush();
        }

        private async Task Flush()
        {
            if (_recorder.HasPending == false)
                return;

            var pending = _recorder.Drain();
            await _outbox.Append(pending, new PolyPersistOutboxTransaction(_inner));
            QueuedFactCount = QueuedFactCount + pending.Count;
        }

        // --- the operations that can carry a fact --------------------------------------------

        public async Task Insert<TDocument>(IDocumentCollection<TDocument> collection, TDocument document) where TDocument : IDocument, new()
        {
            await _inner.Insert(collection, document);
            await DrainIfRecordingRoot(document);
        }

        public async Task Insert<TRow>(IColumnTable<TRow> table, TRow row) where TRow : IRow, new()
        {
            await _inner.Insert(table, row);
            await DrainIfRecordingRoot(row);
        }

        public async Task Insert<TRecord>(ITable<TRecord> table, TRecord record) where TRecord : IRecord, new()
        {
            await _inner.Insert(table, record);
            await DrainIfRecordingRoot(record);
        }

        public async Task Update<TDocument>(IDocumentCollection<TDocument> collection, TDocument document) where TDocument : IDocument, new()
        {
            await _inner.Update(collection, document);
            await DrainIfRecordingRoot(document);
        }

        public async Task Update<TRow>(IColumnTable<TRow> table, TRow row) where TRow : IRow, new()
        {
            await _inner.Update(table, row);
            await DrainIfRecordingRoot(row);
        }

        public async Task Update<TRecord>(ITable<TRecord> table, TRecord record) where TRecord : IRecord, new()
        {
            await _inner.Update(table, record);
            await DrainIfRecordingRoot(record);
        }

        /// <summary>
        /// A delete can produce a fact too - a cancellation is still something that happened - so it
        /// drains like the rest.
        /// </summary>
        public async Task Delete<TDocument>(IDocumentCollection<TDocument> collection, TDocument document) where TDocument : IDocument, new()
        {
            // The operation first, the drain after - as everywhere else. A call that throws must not
            // leave a fact queued for something that did not happen.
            await _inner.Delete(collection, document);
            await DrainIfRecordingRoot(document);
        }

        public async Task Delete<TRow>(IColumnTable<TRow> table, TRow row) where TRow : IRow, new()
        {
            await _inner.Delete(table, row);
            await DrainIfRecordingRoot(row);
        }

        public async Task Delete<TRecord>(ITable<TRecord> table, TRecord record) where TRecord : IRecord, new()
        {
            await _inner.Delete(table, record);
            await DrainIfRecordingRoot(record);
        }

        // --- plain delegation ------------------------------------------------------------------
        // Blobs and change tracking cannot carry a domain fact, so there is nothing to drain here.

        public void AddOriginal<TDocument>(IDocumentCollection<TDocument> collection, TDocument document) where TDocument : IDocument, new()
            => _inner.AddOriginal(collection, document);

        public void AddOriginal<TRow>(IColumnTable<TRow> table, TRow row) where TRow : IRow, new()
            => _inner.AddOriginal(table, row);

        public void AddOriginal<TRecord>(ITable<TRecord> table, TRecord record) where TRecord : IRecord, new()
            => _inner.AddOriginal(table, record);

        public Task AddOriginal<TBlob>(IBlobContainer<TBlob> container, TBlob blob) where TBlob : IBlob, new()
            => _inner.AddOriginal(container, blob);

        public Task Upload<TBlob>(IBlobContainer<TBlob> container, TBlob blob, Stream content) where TBlob : IBlob, new()
            => _inner.Upload(container, blob, content);

        public Task UpdateContent<TBlob>(IBlobContainer<TBlob> container, TBlob blob, Stream content) where TBlob : IBlob, new()
            => _inner.UpdateContent(container, blob, content);

        public Task UpdateMetadata<TBlob>(IBlobContainer<TBlob> container, TBlob blob) where TBlob : IBlob, new()
            => _inner.UpdateMetadata(container, blob);

        public Task Delete<TBlob>(IBlobContainer<TBlob> container, TBlob blob) where TBlob : IBlob, new()
            => _inner.Delete(container, blob);

        public Task Commit() => _inner.Commit();

        /// <summary>
        /// Abandons the unit of work. The queued outbox rows go with it - they were queued, not
        /// written - so a save that did not happen tells nobody anything.
        /// </summary>
        public Task Rollback() => _inner.Rollback();
    }

    public static class OutboxTransactionExtensions
    {
        /// <summary>
        /// Makes a transaction carry the facts out with the state.
        ///
        /// One line at the point the unit of work is created, and from there nothing else has to be
        /// remembered.
        /// </summary>
        public static OutboxTransaction WithOutbox(this ITransaction transaction, IOutboxStore outbox, IEventRecorder recorder)
            => new OutboxTransaction(transaction, outbox, recorder);
    }
}
