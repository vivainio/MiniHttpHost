using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using MiniHttpHost;

internal static partial class Program
{
    private static async Task CheckCompressionAsync()
    {
        var prefix = Prefix();
        string text = new string('x', 100000);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var encoder = new GZipStream(output, CompressionLevel.Fastest, true)) encoder.Write(bytes, 0, bytes.Length);
            compressed = output.ToArray();
        }
        var enabled = new HttpRouteOptions { EnableCompression = true };
        var failedWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var routes = new HttpRouter()
            .MapGet("/text", c => c.TextAsync(text), enabled)
            .MapGet("/plain", c => c.TextAsync(text))
            .MapGet("/empty", c => c.TextAsync(""), enabled)
            .Map("HEAD", "/text", c => c.TextAsync(text), enabled)
            .MapGet("/json", c => c.JsonAsync(new Message { Value = text }), enabled)
            .MapGet("/stream", c => c.StreamAsync(new MemoryStream(bytes), leaveOpen: false), enabled)
            .MapGet("/png", c => c.StreamAsync(new MemoryStream(bytes), "image/png", leaveOpen: false), enabled)
            .MapGet("/mixed-svg", c => c.BytesAsync(bytes, "IMAGE/SVG+XML; charset=utf-8"), enabled)
            .MapGet("/mixed-png", c => c.BytesAsync(bytes, "IMAGE/PNG; parameter=value"), enabled)
            .MapGet("/mixed-cache", c =>
            {
                c.Response.Headers["Cache-Control"] = "public,  NO-TRANSFORM  ";
                return c.TextAsync(text);
            }, enabled)
            .MapGet("/cache-prefix", c =>
            {
                c.Response.Headers["Cache-Control"] = "no-transform-extra";
                return c.TextAsync(text);
            }, enabled)
            .MapGet("/vary-existing", c =>
            {
                c.Response.Headers["Vary"] = "Origin, accept-encoding";
                return c.TextAsync(text);
            }, enabled)
            .MapGet("/encoded", c =>
            {
                c.Response.Headers["Content-Encoding"] = "gzip";
                return c.StreamAsync(new MemoryStream(compressed), leaveOpen: false);
            }, enabled)
            .MapGet("/vary", c =>
            {
                c.Response.Headers["Vary"] = "Origin";
                return c.TextAsync(text);
            }, enabled)
            .MapGet("/etag", c =>
            {
                c.Response.Headers["ETag"] = "\"original\"";
                return c.TextAsync(text);
            }, enabled)
            .MapGet("/cache", c =>
            {
                c.Response.Headers["Cache-Control"] = "public, no-transform";
                return c.TextAsync(text);
            }, enabled)
            .MapGet("/no-content", c =>
            {
                c.Response.StatusCode = 204;
                c.Response.ContentLength64 = 0;
                return Task.CompletedTask;
            }, enabled)
            .MapGet("/error", c => throw new Exception("before body"), enabled)
            .MapGet("/late-error", async c =>
            {
                c.Response.ContentType = "text/plain";
                c.Response.SendChunked = true;
                await c.ResponseBody.WriteAsync(bytes, 0, bytes.Length, c.RequestAborted);
                throw new Exception("compressed write failure");
            }, enabled)
            .MapGet("/custom", c => c.TextAsync(text), new HttpRouteOptions {
                EnableCompression = true, CompressionProvider = new TestCompressionProvider()
            });
        // Registration snapshots the opt-in flag, not later mutations of the options object.
        enabled.EnableCompression = false;
        using (var server = new MiniWebServer(prefix, new HttpPipeline().UseExceptionHandler((c, e) => {
            if (e.Message == "compressed write failure") failedWrite.TrySetResult(c.HasStarted);
        }).Build(routes.HandleAsync)))
        using (var client = new HttpClient(new HttpClientHandler {
            UseProxy = false, AutomaticDecompression = DecompressionMethods.None
        }) { BaseAddress = new Uri(prefix), Timeout = TimeSpan.FromSeconds(5) })
        {
            await server.StartAsync();
            foreach (var coding in new[] { "gzip", "GZip", "br, gzip;q=0.5", "*;q=0.5", "gzip;q=0.8, identity;q=0.2" })
            {
                using (var response = await CompressionRequestAsync(client, "text", coding))
                {
                    Check(response.Content.Headers.ContentEncoding.Single() == "gzip", "Gzip negotiation: " + coding);
                    Check(response.Headers.TransferEncodingChunked == true && !response.Content.Headers.ContentLength.HasValue,
                        "Compressed response does not send uncompressed length");
                    byte[] wire = await response.Content.ReadAsByteArrayAsync();
                    Check(wire.Length < bytes.Length && DecodeGzip(wire).SequenceEqual(bytes), "Gzip round trip and trailer");
                }
            }
            foreach (var coding in new[] { null, "", "gzip;q=0", "gzip;q=0, *;q=1", "br", "gzip;q=0.2, identity;q=1" })
            {
                using (var response = await CompressionRequestAsync(client, "text", coding))
                {
                    Check(!response.Content.Headers.ContentEncoding.Any(), "Identity negotiation: " + coding);
                    Check(await response.Content.ReadAsStringAsync() == text, "Identity body unchanged");
                    Check(response.Headers.Vary.Contains("Accept-Encoding"), "Identity variant includes Vary");
                }
            }
            foreach (var path in new[] { "plain", "png", "etag", "cache", "mixed-png", "mixed-cache" })
                using (var response = await CompressionRequestAsync(client, path, "gzip"))
                {
                    Check(!response.Content.Headers.ContentEncoding.Any(), "Skip compression: " + path);
                    Check((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes), "Skipped body unchanged: " + path);
                }
            foreach (var path in new[] { "mixed-svg", "cache-prefix" })
                using (var response = await CompressionRequestAsync(client, path, "gzip"))
                    Check(DecodeGzip(await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes), "Span parsing preserves compressible response: " + path);
            using (var response = await CompressionRequestAsync(client, "vary-existing", "gzip"))
                Check(response.Headers.Vary.Count(v => v.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)) == 1,
                    "Vary token scan avoids case-insensitive duplicates");
            using (var response = await CompressionRequestAsync(client, "stream", "gzip"))
                Check(DecodeGzip(await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes), "Stream helper compression");
            using (var response = await CompressionRequestAsync(client, "json", "gzip"))
                Check(Encoding.UTF8.GetString(DecodeGzip(await response.Content.ReadAsByteArrayAsync())).Contains(text), "JSON helper compression");
            using (var response = await CompressionRequestAsync(client, "encoded", "gzip"))
                Check((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(compressed), "Already-encoded body is not recompressed");
            using (var response = await CompressionRequestAsync(client, "vary", "gzip"))
                Check(response.Headers.Vary.Contains("Origin") && response.Headers.Vary.Contains("Accept-Encoding"), "Merge existing Vary");
            foreach (var path in new[] { "empty", "no-content" })
                using (var response = await CompressionRequestAsync(client, path, "gzip"))
                    Check(!response.Content.Headers.ContentEncoding.Any() && (await response.Content.ReadAsByteArrayAsync()).Length == 0, "No encoded body: " + path);
            using (var request = new HttpRequestMessage(HttpMethod.Head, "text"))
            {
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
                using (var response = await client.SendAsync(request))
                    Check((await response.Content.ReadAsByteArrayAsync()).Length == 0, "HEAD never emits a compression trailer");
            }
            using (var request = new HttpRequestMessage(HttpMethod.Get, "text"))
            {
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 10);
                using (var response = await client.SendAsync(request))
                    Check(!response.Content.Headers.ContentEncoding.Any(), "Range requests aren't transformed");
            }
            using (var response = await CompressionRequestAsync(client, "text", "br, identity;q=0"))
                Check((int)response.StatusCode == 406, "Reject unsupported coding when identity forbidden");
            using (var response = await CompressionRequestAsync(client, "png", "gzip, identity;q=0"))
                Check((int)response.StatusCode == 406, "Ineligible response doesn't violate identity exclusion");
            using (var response = await CompressionRequestAsync(client, "error", "gzip"))
            {
                Check(response.StatusCode == HttpStatusCode.InternalServerError, "Pre-write exception remains a normal error");
                Check(!response.Content.Headers.ContentEncoding.Any(), "Pre-write exception doesn't leave stale coding");
            }
            using (var response = await CompressionRequestAsync(client, "custom", "test-gzip"))
            {
                Check(response.Content.Headers.ContentEncoding.Single() == "test-gzip", "Custom provider selected");
                Check(DecodeGzip(await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes), "Custom provider owns encoder creation");
            }
            try
            {
                using (var response = await CompressionRequestAsync(client, "late-error", "gzip"))
                    try { await response.Content.ReadAsByteArrayAsync(); } catch (HttpRequestException) { }
            }
            catch (HttpRequestException) { }
            await WithDeadline(failedWrite.Task);
            Check(failedWrite.Task.Result, "Accepted compressed writes count as a started response");
            using (var response = await CompressionRequestAsync(client, "plain", null))
                Check(await response.Content.ReadAsStringAsync() == text, "Compression failure doesn't stop the server");
            await server.StopAsync();
        }
    }

    private static Task<HttpResponseMessage> CompressionRequestAsync(HttpClient client, string path, string coding)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (coding != null) request.Headers.TryAddWithoutValidation("Accept-Encoding", coding);
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static byte[] DecodeGzip(byte[] wire)
    {
        using (var input = new MemoryStream(wire))
        using (var decoder = new GZipStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            decoder.CopyTo(output);
            return output.ToArray();
        }
    }

    private sealed class TestCompressionProvider : IResponseCompressionProvider
    {
        public string EncodingName => "test-gzip";
        public Stream CreateCompressionStream(Stream destination) => new GZipStream(destination, CompressionLevel.Fastest, true);
    }
}
