using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace MiniHttpHost.Patterns.ProtobufDispatch
{
    internal sealed class MessageClient : IDisposable
    {
        private readonly HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false });
        private readonly string endpoint;

        public MessageClient(string prefix) { endpoint = prefix.TrimEnd('/') + "/messages"; }

        public async Task<Customer> GetAsync(int id)
        {
            return await SendAsync<GetCustomer, Customer>(MessageTypes.GetCustomer,
                new GetCustomer { Id = id }, MessageTypes.Customer);
        }

        public async Task<Customer> UpdateAsync(int id, string name)
        {
            return await SendAsync<UpdateCustomer, Customer>(MessageTypes.UpdateCustomer,
                new UpdateCustomer { Id = id, Name = name }, MessageTypes.Customer);
        }

        private async Task<TReply> SendAsync<TRequest, TReply>(string type, TRequest request, string replyType)
        {
            var correlation = Guid.NewGuid().ToString("N");
            using (var response = await PostAsync(Wire.Encode(new Envelope {
                MessageType = type, CorrelationId = correlation, Payload = Wire.Encode(request)
            })))
            {
                var mediaType = response.Content.Headers.ContentType;
                if (mediaType == null || !string.Equals(mediaType.MediaType, Wire.MediaType, StringComparison.OrdinalIgnoreCase))
                {
                    response.EnsureSuccessStatusCode();
                    throw new InvalidOperationException("Expected a protobuf response.");
                }
                var reply = Wire.Decode<Envelope>(await response.Content.ReadAsByteArrayAsync());
                if (reply.CorrelationId != correlation) throw new InvalidOperationException("Correlation ID mismatch.");
                if (reply.MessageType == MessageTypes.Error)
                {
                    var error = Wire.Decode<Error>(reply.Payload);
                    throw new InvalidOperationException((int)response.StatusCode + " " + error.Code + ": " + error.Message);
                }
                response.EnsureSuccessStatusCode();
                if (reply.MessageType != replyType) throw new InvalidOperationException("Unexpected reply type.");
                return Wire.Decode<TReply>(reply.Payload);
            }
        }

        // Also used by smoke checks to send deliberately invalid wire messages.
        internal async Task<HttpResponseMessage> PostAsync(byte[] body, string mediaType = Wire.MediaType)
        {
            using (var content = new ByteArrayContent(body))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                return await http.PostAsync(endpoint, content);
            }
        }

        public void Dispose() { http.Dispose(); }
    }
}
