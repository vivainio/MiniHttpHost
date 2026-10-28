using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace MiniHttpHost.Patterns
{
    internal static class SoapEndpoint
    {
        private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
        private static readonly XNamespace Contract = "urn:minihttp:hello";

        public static async Task HandleAsync(HttpContext context)
        {
            var mediaType = (context.Request.ContentType ?? "").Split(';')[0].Trim();
            if (!string.Equals(mediaType, "text/xml", StringComparison.OrdinalIgnoreCase))
            {
                await context.TextAsync("Expected SOAP 1.1 text/xml\n", 415);
                return;
            }

            XElement reply;
            var status = 200;
            try
            {
                // Bound buffering keeps network reads asynchronous. XML parsing is
                // synchronous, but only sees this small, route-limited document.
                using (var buffer = new MemoryStream())
                {
                    await context.RequestBody.CopyToAsync(buffer, 4096, context.RequestAborted);
                    buffer.Position = 0;
                    var settings = new XmlReaderSettings {
                        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                        MaxCharactersInDocument = 65536
                    };
                    using (var reader = XmlReader.Create(buffer, settings))
                    {
                        var envelope = XDocument.Load(reader).Root;
                        if (envelope == null || envelope.Name != Soap + "Envelope")
                            throw new XmlException("Expected SOAP 1.1 envelope.");
                        var headers = envelope.Element(Soap + "Header");
                        if (headers != null && headers.Elements().Any())
                        {
                            await WriteAsync(context, Fault("MustUnderstand", "SOAP headers are not supported."), 500);
                            return;
                        }
                        var bodies = envelope.Elements(Soap + "Body").ToArray();
                        var operations = bodies.Length == 1 ? bodies[0].Elements().ToArray() : new XElement[0];
                        var action = (context.Request.Headers["SOAPAction"] ?? "").Trim().Trim('"');
                        // Explicit action AND body dispatch; no reflection or caller-selected CLR types.
                        if (action != "urn:minihttp:hello/SayHello" || operations.Length != 1 ||
                            operations[0].Name != Contract + "SayHello")
                            throw new XmlException("Unknown action or operation.");
                        var fields = operations[0].Elements().ToArray();
                        if (fields.Length != 1 || fields[0].Name != Contract + "name" || fields[0].HasElements)
                            throw new XmlException("Expected one name field.");
                        reply = new XElement(Contract + "SayHelloResponse",
                            new XElement(Contract + "SayHelloResult", "Hello, " + fields[0].Value + "!"));
                    }
                }
            }
            catch (XmlException)
            {
                status = 500;
                reply = Fault("Client", "Invalid SOAP request or unsupported operation.");
            }
            await WriteAsync(context, reply, status);
        }

        private static XElement Fault(string code, string message)
        {
            return new XElement(Soap + "Fault", new XElement("faultcode", "s:" + code),
                new XElement("faultstring", message));
        }

        private static async Task WriteAsync(HttpContext context, XElement body, int status)
        {
            var envelope = new XElement(Soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", Soap),
                new XElement(Soap + "Body", body));
            var bytes = Encoding.UTF8.GetBytes(envelope.ToString(SaveOptions.DisableFormatting));
            using (var stream = new MemoryStream(bytes))
                await context.StreamAsync(stream, "text/xml; charset=utf-8", bytes.Length, statusCode: status);
        }
    }
}
