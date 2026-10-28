using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ProtoBuf;

namespace MiniHttpHost.Patterns.ProtobufDispatch
{
    internal sealed class MessageEndpoint
    {
        private readonly Dictionary<string, Func<byte[], Envelope>> handlers =
            new Dictionary<string, Func<byte[], Envelope>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, string> customers = new ConcurrentDictionary<int, string>();

        public MessageEndpoint()
        {
            customers[1] = "Ada";
            Register<GetCustomer, Customer>(MessageTypes.GetCustomer, MessageTypes.Customer, Get);
            Register<UpdateCustomer, Customer>(MessageTypes.UpdateCustomer, MessageTypes.Customer, Update);
        }

        private void Register<TRequest, TReply>(string requestType, string replyType, Func<TRequest, TReply> handle)
        {
            handlers.Add(requestType, bytes => {
                TRequest request;
                try { request = Wire.Decode<TRequest>(bytes); }
                catch (Exception error) when (IsMalformed(error))
                {
                    throw new MessageError(400, "malformed_payload", "Invalid protobuf payload.");
                }
                return new Envelope { MessageType = replyType, Payload = Wire.Encode(handle(request)) };
            });
        }

        public async Task HandleAsync(HttpContext context)
        {
            Envelope request = null;
            Envelope reply;
            var status = 200;
            try
            {
                var mediaType = (context.Request.ContentType ?? "").Split(';')[0].Trim();
                if (!string.Equals(mediaType, Wire.MediaType, StringComparison.OrdinalIgnoreCase))
                    throw new MessageError(415, "unsupported_media_type", "Expected application/protobuf.");
                using (var buffer = new MemoryStream())
                {
                    // RequestBody enforces the route's limit, including chunked requests.
                    await context.RequestBody.CopyToAsync(buffer, 4096, context.RequestAborted);
                    try { request = Wire.Decode<Envelope>(buffer.ToArray()); }
                    catch (Exception error) when (IsMalformed(error))
                    {
                        throw new MessageError(400, "malformed_envelope", "Invalid protobuf envelope.");
                    }
                }
                if (request == null || string.IsNullOrWhiteSpace(request.MessageType) ||
                    string.IsNullOrWhiteSpace(request.CorrelationId) || request.CorrelationId.Length > 128)
                    throw new MessageError(400, "invalid_envelope", "Message type and correlation ID (1–128 characters) are required.");
                Func<byte[], Envelope> handler;
                if (!handlers.TryGetValue(request.MessageType, out handler))
                    throw new MessageError(400, "unknown_message_type", "Message type is not registered.");
                context.RequestAborted.ThrowIfCancellationRequested();
                reply = handler(request.Payload);
            }
            catch (MessageError error)
            {
                status = error.Status;
                reply = new Envelope { MessageType = MessageTypes.Error,
                    Payload = Wire.Encode(new Error { Code = error.Code, Message = error.Message }) };
            }
            reply.CorrelationId = request != null && request.CorrelationId != null && request.CorrelationId.Length <= 128
                ? request.CorrelationId : "";
            using (var output = new MemoryStream(Wire.Encode(reply)))
                await context.StreamAsync(output, Wire.MediaType, output.Length, statusCode: status);
        }

        private Customer Get(GetCustomer request)
        {
            if (request == null || request.Id <= 0)
                throw new MessageError(400, "invalid_customer", "A positive customer ID is required.");
            string name;
            if (!customers.TryGetValue(request.Id, out name))
                throw new MessageError(404, "customer_not_found", "Customer does not exist.");
            return new Customer { Id = request.Id, Name = name };
        }

        private Customer Update(UpdateCustomer request)
        {
            if (request == null || request.Id <= 0 || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
                throw new MessageError(400, "invalid_customer", "A positive ID and name (1–200 characters) are required.");
            // Update existing customers only; this demo never deletes entries.
            if (!customers.ContainsKey(request.Id))
                throw new MessageError(404, "customer_not_found", "Customer does not exist.");
            customers[request.Id] = request.Name;
            return new Customer { Id = request.Id, Name = request.Name };
        }

        private static bool IsMalformed(Exception error)
        {
            return error is ProtoException || error is EndOfStreamException || error is OverflowException;
        }

        private sealed class MessageError : Exception
        {
            public int Status { get; }
            public string Code { get; }
            public MessageError(int status, string code, string message) : base(message)
            { Status = status; Code = code; }
        }
    }
}
