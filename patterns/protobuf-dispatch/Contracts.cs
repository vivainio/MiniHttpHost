using System.IO;
using ProtoBuf;

namespace MiniHttpHost.Patterns.ProtobufDispatch
{
    // Stable wire identifiers, independent of CLR class and assembly names.
    internal static class MessageTypes
    {
        public const string GetCustomer = "customer.get.v1";
        public const string UpdateCustomer = "customer.update.v1";
        public const string Customer = "customer.v1";
        public const string Error = "error.v1";
    }

    [ProtoContract]
    public sealed class Envelope
    {
        [ProtoMember(1)] public string MessageType { get; set; }
        [ProtoMember(2)] public string CorrelationId { get; set; }
        [ProtoMember(3)] public byte[] Payload { get; set; }
    }

    [ProtoContract]
    public sealed class GetCustomer
    {
        [ProtoMember(1)] public int Id { get; set; }
    }

    [ProtoContract]
    public sealed class UpdateCustomer
    {
        [ProtoMember(1)] public int Id { get; set; }
        [ProtoMember(2)] public string Name { get; set; }
    }

    [ProtoContract]
    public sealed class Customer
    {
        [ProtoMember(1)] public int Id { get; set; }
        [ProtoMember(2)] public string Name { get; set; }
    }

    [ProtoContract]
    public sealed class Error
    {
        [ProtoMember(1)] public string Code { get; set; }
        [ProtoMember(2)] public string Message { get; set; }
    }

    internal static class Wire
    {
        public const string MediaType = "application/protobuf";

        public static byte[] Encode<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                Serializer.Serialize(stream, value);
                return stream.ToArray();
            }
        }

        public static T Decode<T>(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes ?? new byte[0]))
                return Serializer.Deserialize<T>(stream);
        }
    }
}
