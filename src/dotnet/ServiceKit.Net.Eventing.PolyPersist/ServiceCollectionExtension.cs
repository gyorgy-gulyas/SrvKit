using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PolyPersist;

namespace ServiceKit.Net.Eventing.PolyPersistStores
{
    public static class ServiceCollectionExtension
    {
        /// <summary>
        /// Keeps the outbox and the inbox in PolyPersist.
        ///
        /// WHERE they are kept is the decision that sets the atomicity guarantee, and it is made
        /// here rather than in the model: put the outbox collection in the SAME store the domain
        /// writes to and the fact and the state land in one commit. Put it somewhere else and the
        /// platform still delivers, but the window between the two is real.
        /// </summary>
        public static IServiceCollection UseEventing_PolyPersist(
            this IServiceCollection services,
            Func<IServiceProvider, IDocumentCollection<OutboxRecord>> outbox,
            Func<IServiceProvider, IDocumentCollection<InboxRecord>> inbox,
            Action<PolyPersistOutboxOptions> configure = null)
        {
            var options = new PolyPersistOutboxOptions();
            configure?.Invoke(options);

            services.TryAddSingleton(options);
            services.TryAddSingleton<IOutboxStore>(sp => new PolyPersistOutboxStore(outbox(sp), options));
            services.TryAddSingleton<IInboxStore>(sp => new PolyPersistInboxStore(inbox(sp)));

            return services;
        }

        /// <summary>
        /// Wraps a PolyPersist transaction so the outbox can join it.
        ///
        /// What a repository passes to <see cref="IOutboxStore.Append"/> inside its save.
        /// </summary>
        public static IOutboxTransaction AsOutboxTransaction(this ITransaction transaction)
            => new PolyPersistOutboxTransaction(transaction);
    }
}
