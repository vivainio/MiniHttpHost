using System;
using System.Threading.Tasks;

namespace MiniHttpHost.Patterns.ProtobufDispatch
{
    internal static class Checks
    {
        public static async Task RunAsync(MessageClient client)
        {
            Require((await client.GetAsync(1)).Name == "Ada", "Initial customer");
            Require((await client.UpdateAsync(1, "Grace")).Name == "Grace", "Update reply");
            Require((await client.GetAsync(1)).Name == "Grace", "Updated state");
            await ErrorAsync(client, Wrap("unknown", new byte[] { 0x80 }), 400, "unknown_message_type", "check");
            await ErrorAsync(client, Wrap(MessageTypes.GetCustomer, new byte[] { 0x80 }), 400, "malformed_payload", "check");
            await ErrorAsync(client, new byte[] { 0x80 }, 400, "malformed_envelope", "");
            await ErrorAsync(client, Wire.Encode(new Envelope { MessageType = MessageTypes.GetCustomer }), 400, "invalid_envelope", "");
            await ErrorAsync(client, Wrap(MessageTypes.GetCustomer, Wire.Encode(new GetCustomer { Id = 99 })),
                404, "customer_not_found", "check");
            await ErrorAsync(client, Wrap(MessageTypes.UpdateCustomer, Wire.Encode(new UpdateCustomer { Id = 1 })),
                400, "invalid_customer", "check");
            await ErrorAsync(client, new byte[0], 415, "unsupported_media_type", "", "text/plain");
            using (var response = await client.PostAsync(new byte[65537]))
                Require((int)response.StatusCode == 413, "Body limit");
            Console.WriteLine("Passed: typed dispatch, update/read, correlation, unknown types, malformed envelope/payload, validation, 404, 415, 413.");
        }

        private static byte[] Wrap(string type, byte[] payload)
        {
            return Wire.Encode(new Envelope { MessageType = type, CorrelationId = "check", Payload = payload });
        }

        private static async Task ErrorAsync(MessageClient client, byte[] body, int status, string code,
            string correlation, string mediaType = Wire.MediaType)
        {
            using (var response = await client.PostAsync(body, mediaType))
            {
                Require((int)response.StatusCode == status, code + " status");
                var envelope = Wire.Decode<Envelope>(await response.Content.ReadAsByteArrayAsync());
                Require(envelope.MessageType == MessageTypes.Error, code + " reply type");
                Require((envelope.CorrelationId ?? "") == correlation, code + " correlation");
                Require(Wire.Decode<Error>(envelope.Payload).Code == code, code + " error code");
            }
        }

        private static void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Check failed: " + label);
        }
    }
}
