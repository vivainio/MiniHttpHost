# Sharing a process with WCF

Run MiniHttpHost on a separate port alongside the existing WCF service. Each
listener has its own request queue, authentication configuration, and lifetime.
WCF binding authentication does not protect MiniHttpHost routes; configure
[authentication and authorization](security.md) separately.

MiniHttpHost suppresses ambient execution-context capture for its worker loops,
so startup inside a WCF operation does not retain that operation's principal or
logical call context. Use `context.NativeContext.User` for the request identity.

## Separate ports and independent lifetimes

Configure MiniHttpHost with its own port and URL prefix. The Windows integration
check hosts WCF and MiniHttpHost in one process on different ports, verifies both
respond, then verifies WCF still responds after MiniHttpHost shuts down.

URL ACLs and HTTP.sys TLS certificate bindings are system configuration, not
properties owned by one framework instance. Coordinate them with the existing
application deployment. See [WCF HTTP/HTTPS configuration](https://learn.microsoft.com/en-us/dotnet/framework/wcf/feature-details/configuring-http-and-https).

## Shared CLR capacity, different scheduling

WCF's internal [ActionItem](https://github.com/microsoft/referencesource/blob/main/System.ServiceModel.Internals/System/Runtime/ActionItem.cs)
queues work through [IOThreadScheduler](https://github.com/microsoft/referencesource/blob/main/System.ServiceModel.Internals/System/Runtime/IOThreadScheduler.cs).
That scheduler maintains its own queues and posts native-overlapped callbacks to
the CLR thread pool. It is not a separate pool of dedicated WCF threads.

MiniHttpHost uses `Task.Run` and asynchronous I/O on the shared CLR infrastructure.
This shares process capacity, not WCF's queue, priorities, service-instance model,
or concurrent-call throttle. Don't treat matching numeric limits as identical
scheduling. CPU-heavy or blocking handlers can still compete with WCF.

MiniHttpHost does not change global thread-pool limits. Its fixed worker loops
couple accept capacity to active handler capacity: long transfers consume the
same budget as ordinary calls. See [lifetime and concurrency](lifetime.md).

## Streaming behavior to account for

The framework's [HttpListener request stream](https://github.com/microsoft/referencesource/blob/main/System/net/System/Net/_ListenerRequestStream.cs)
uses a native body-read flag with value 1 outside opaque/WebSocket mode. For
`HttpReceiveRequestEntityBody`, this is the
[fill-buffer behavior](https://learn.microsoft.com/en-us/windows/win32/api/http/nf-http-httpreceiverequestentitybody).
A read can wait for the requested chunk to fill or for the request to end; it
need not return as soon as a smaller amount arrives.

Use the copy-buffer guidance in [request context and streaming](streaming.md).
Modern .NET may round pooled buffers up, so actual reads can exceed the requested
buffer size.

The framework's [response stream](https://github.com/microsoft/referencesource/blob/main/System/net/System/Net/_ListenerResponseStream.cs)
implements `Flush`/`FlushAsync` as no-ops. Flushing an application-side buffered
writer can still matter, but flushing the native response stream cannot force a
pending request read to complete.
