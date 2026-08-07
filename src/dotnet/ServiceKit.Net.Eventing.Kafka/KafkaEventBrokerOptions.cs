using System.Runtime.CompilerServices;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

[assembly: InternalsVisibleTo("ServiceKit.Net.Eventing.Kafka.Tests")]

namespace ServiceKit.Net.Eventing.Kafka
{
    /// <summary>
    /// Everything about Kafka that the model is not allowed to know.
    ///
    /// Topic names, partition counts, retention, security - none of it appears in a .d3 file, and
    /// this class is where it does appear instead. Creating the topics themselves is not this
    /// library's job either: that is installation, and installation is the Orbit's.
    /// </summary>
    public sealed class KafkaEventBrokerOptions
    {
        public string BootstrapServers { get; set; } = "localhost:9092";

        /// <summary>
        /// Explicit channel-to-topic mapping, for the deployments where the topic already exists
        /// with a name of somebody else's choosing.
        /// </summary>
        public Dictionary<string, string> Topics { get; } = new(StringComparer.Ordinal);

        /// <summary>Prefixes the derived topic name - useful when several environments share a cluster.</summary>
        public string TopicPrefix { get; set; } = string.Empty;

        public int ProducerRetries { get; set; } = 5;

        /// <summary>How long to wait after a failed delivery, multiplied by the attempt number.</summary>
        public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>The ceiling on that wait: a tight retry loop is a denial of service aimed at oneself.</summary>
        public TimeSpan MaxRetryBackoff { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How often a repeating failure is logged after the first one.
        ///
        /// The first attempt always logs. After that a stalled partition would otherwise fill the
        /// log with the same error several times a second and bury everything else.
        /// </summary>
        public int LogEveryNthFailure { get; set; } = 20;

        /// <summary>
        /// The topic a logical channel lives on.
        ///
        /// An explicit map wins; otherwise the channel name with dots turned into dashes, because
        /// Kafka warns about topics that mix '.' and '_' - they collide in metric names.
        /// </summary>
        public string TopicFor(string channel)
        {
            if (Topics.TryGetValue(channel, out var mapped) == true)
                return mapped;

            return TopicPrefix + channel.Replace('.', '-');
        }

        /// <summary>Escape hatches for the settings this class has no opinion about - security, above all.</summary>
        public Action<ProducerConfig> ConfigureProducer { get; set; }
        public Action<ConsumerConfig> ConfigureConsumer { get; set; }
    }

    public static class ServiceCollectionExtension
    {
        /// <summary>
        /// Puts the facts on Kafka.
        ///
        /// Nothing above this line changes: the generated code, the recorder, the outbox and the
        /// relay are the same as with the in-memory broker. That is the point of the adapter being
        /// three methods wide.
        /// </summary>
        public static IServiceCollection UseEventing_Kafka(this IServiceCollection services, Action<KafkaEventBrokerOptions> configure)
        {
            var options = new KafkaEventBrokerOptions();
            configure?.Invoke(options);

            services.TryAddSingleton(options);
            services.TryAddSingleton<IEventBroker, KafkaEventBroker>();

            return services;
        }
    }
}
