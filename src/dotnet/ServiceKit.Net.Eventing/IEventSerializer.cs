using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ServiceKit.Net.Eventing
{
    /// <summary>
    /// How a fact becomes bytes and back.
    ///
    /// Replaceable, but with one obvious default - because a serializer configured differently on
    /// the two ends is the kind of bug that only shows up in production, and the platform has been
    /// bitten by exactly that before (a generated REST client with its own JSON options).
    /// </summary>
    public interface IEventSerializer
    {
        string ContentType { get; }
        string Serialize(IDomainEvent @event);
        object Deserialize(string payload, Type eventType);
    }

    /// <summary>
    /// JSON, with the settings fixed in one place so both ends cannot drift apart.
    /// </summary>
    public sealed class JsonEventSerializer : IEventSerializer
    {
        /// <summary>
        /// Property names are NOT camel-cased and NOT case-insensitive on read.
        ///
        /// The names in the payload are the names in the model, and a consumer that reads a
        /// different casing is reading a different contract. Being lenient here would let a
        /// mismatch survive until the one field that differs actually carries a value.
        /// </summary>
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions()
        {
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            {
                Modifiers = { KeepTheRoutingConstantsOffTheWire },
            },
        };

        /// <summary>
        /// SchemaId and Channel are how the platform routes a fact; they are not part of what the
        /// fact means. Leaving them in the payload would put two plumbing fields into a contract
        /// that other teams read - and worse, would let a consumer start depending on them.
        ///
        /// Done here rather than by asking the emitter for [JsonIgnore], because a rule the
        /// serializer enforces holds whatever shape the generated class happens to take.
        /// </summary>
        private static void KeepTheRoutingConstantsOffTheWire(JsonTypeInfo typeInfo)
        {
            if (typeof(IDomainEvent).IsAssignableFrom(typeInfo.Type) == false || typeInfo.Type.IsInterface == true)
                return;

            var map = typeInfo.Type.GetInterfaceMap(typeof(IDomainEvent));
            var routingGetters = new HashSet<MethodInfo>(map.TargetMethods);

            for (int i = typeInfo.Properties.Count - 1; i >= 0; i--)
            {
                // Matched through the interface map rather than by name: an event whose business
                // data happens to be called 'channel' keeps its field.
                if (typeInfo.Properties[i].AttributeProvider is PropertyInfo property
                    && property.GetMethod != null
                    && routingGetters.Contains(property.GetMethod) == true)
                {
                    typeInfo.Properties.RemoveAt(i);
                }
            }
        }

        public string ContentType => "application/json";

        public string Serialize(IDomainEvent @event)
        {
            // The runtime type, not IDomainEvent: serializing through the interface would write an
            // empty object, since the interface has only the two routing members.
            return JsonSerializer.Serialize(@event, @event.GetType(), Options);
        }

        public object Deserialize(string payload, Type eventType)
        {
            return JsonSerializer.Deserialize(payload, eventType, Options);
        }
    }
}
