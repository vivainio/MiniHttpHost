# Request context and streaming

Every handler receives one `MiniHttpHost.HttpContext`:

| Member | Purpose |
| --- | --- |
| `Request`, `Response` | Native HttpListener metadata, status, and headers |
| `RequestBody`, `ResponseBody` | Streaming bodies with byte-limit/start tracking |
| `RouteValues` | Decoded parameters for the selected route |
| `Items` | Per-request state shared by middleware and handlers |
| `RequestAborted` | Cancellation from host abort or the selected route's deadline |
| `HasStarted` | Records a response-body write or flush; errors must no longer replace the response |
| `NativeContext` | Escape hatch to the underlying HttpListener context |

Use `RequestBody` and `ResponseBody` for body I/O. Reading `Request.InputStream`
or writing `Response.OutputStream` directly bypasses the wrappers' byte counting
or response-start tracking. Native HttpListener does not expose a client-disconnect
token: peer disconnections surface through failed I/O, rather than guaranteed
proactive cancellation of `RequestAborted`.

The response helpers are extension methods on the context:

| Helper | Behavior |
| --- | --- |
| `TextAsync(text, statusCode: 200)` | UTF-8 plain text |
| `JsonAsync(value, statusCode: 200)` | Framework `DataContractJsonSerializer`; buffers small JSON responses |
| `StreamAsync(source, contentType, contentLength, leaveOpen: true)` | Incremental copy; defaults to a 4 KiB buffer and caller-owned stream |
| `FileAsync(path, contentType)` | Opens an asynchronous file stream, sends it, and disposes it |

`StreamAsync` infers remaining length from seekable streams. Otherwise, provide a
length or let it use chunked encoding. `FileAsync` sets content length. These two
helpers do not buffer the whole payload. JSON follows the framework serializer's
contracts: use public DTOs or `[DataContract]` / `[DataMember]` types. It is not an
automatic model-binding or arbitrary anonymous-object serialization API.

The 4 KiB default measured fastest for downloads and small uploads. For large
uploads, `StreamAsync(source, bufferSize: 81920)` measured faster; `FileAsync`
also accepts `bufferSize`. Framework HttpListener can wait to fill a requested
body-read buffer, so this affects first-chunk latency as well as throughput.
Flushing the response does not unblock a pending request read.

For lower-level streaming, set response metadata before the first write:

```csharp
public async Task UploadAsync(MiniHttpHost.HttpContext context)
{
    using (var destination = OpenApplicationStorage())
    {
        await context.RequestBody.CopyToAsync(destination, 81920,
            context.RequestAborted).ConfigureAwait(false);
    }
    context.Response.StatusCode = 204;
    context.Response.ContentLength64 = 0;
}
```

`OpenApplicationStorage` represents your application's writable stream. For
incremental responses, write to `ResponseBody` and flush after each message.
Await all stream work inside the handler: the server closes the response when
the handler returns. Helpers suppress the body for explicitly registered HEAD
routes. Range requests, resumable downloads, and static-file serving are not
implemented.

The [console example](https://github.com/vivainio/MiniHttpHost/blob/main/examples/ConsoleHost/BinaryEndpoints.cs) registers instance
upload/download handlers. It writes uploads to a temporary file, publishes only
completed uploads, and streams downloads from disk. Its single resource is
serialized so a download never reads a partially uploaded replacement.
