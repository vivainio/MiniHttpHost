# Response compression

Compression is opt-in per route. It is implemented with streams, without buffering
an entire response or adding runtime NuGet dependencies.

```csharp
routes.MapGet("/report", api.ReportAsync,
    new HttpRouteOptions { EnableCompression = true });

routes.MapGet("/archive", api.DownloadAsync,
    new HttpRouteOptions { EnableCompression = false });
```

The built-in provider uses gzip with `CompressionLevel.Fastest`. Choose a different
level per route:

```csharp
using System.IO.Compression;

routes.MapGet("/report", api.ReportAsync, new HttpRouteOptions {
    EnableCompression = true,
    CompressionProvider = new GzipCompressionProvider(CompressionLevel.Optimal)
});
```

## Negotiation and streaming

The selected provider's coding is negotiated against `Accept-Encoding`, including
quality values, explicit exclusions, wildcard support, and an explicit preference
for identity. Requests without an accepted coding use the original body. If both
the provider and identity are excluded, the response is 406.

Compressed output uses `Content-Encoding` and chunked transfer rather than the
uncompressed `Content-Length`. Both identity and compressed variants carry
`Vary: Accept-Encoding`, preserving existing Vary values. Text, JSON, file, and
stream helpers all write through the same compression stream. The encoder is
finished before the native response closes.

HEAD, bodyless statuses, range responses/requests, existing `Content-Encoding`,
ETag/digest-bearing responses, and `Cache-Control: no-transform` are not transformed.
Known compressed media types such as ZIP, gzip, PNG/JPEG, audio/video, and PDF are
skipped. Event streams are skipped to avoid compression buffering their messages.
For `application/octet-stream`, the route's opt-in is the application's decision:
the server cannot infer whether arbitrary binary bytes are already compressed.

Write through `context.ResponseBody`; native `Response.OutputStream` bypasses the
feature. Compression covers the selected route and its group middleware. Global
middleware must not append body bytes after the router returns. Errors before a
body starts remain normal error responses; failures after accepted body writes
abort the transfer. Cancellation also aborts rather than finishing a partial body.

## Pluggable providers

The core contract is intentionally small:

```csharp
public interface IResponseCompressionProvider
{
    string EncodingName { get; }
    Stream CreateCompressionStream(Stream destination);
}
```

A provider declares a coding token such as `gzip` and creates a fresh encoder for
each response. Disposing the encoder must finish its trailer and leave the
provided destination open. Providers are shared across requests and should be
immutable/thread-safe. A route selects one provider; this is not an automatic
multi-codec preference list.

Applications can implement a Brotli or Zstandard provider using an optional codec
package. The framework build has no built-in Brotli/Zstandard dependency, and the
core doesn't install one. Custom providers inherit the same negotiation and
streaming behavior.

## IIS and incoming bodies

HttpListener uses HTTP.sys directly, so IIS compression modules do not run for
these endpoints. A reverse proxy may compress instead; coordinate which component
owns compression to avoid duplicate work.

This feature compresses responses only. It does not automatically decompress
request bodies, configure IIS, or implement caching/range handling. A `.gz` file
download is a separate representation, not transparent HTTP content encoding.
