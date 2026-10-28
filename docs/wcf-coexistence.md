# Sharing a process with WCF

The intended setup runs MiniHttpHost alongside an existing WCF service in the
same process, with MiniHttpHost listening on a separate port. The concerns are
shared process resources, independent listener lifetimes, authentication, and
streaming behavior.

This review covers Microsoft's .NET Framework reference implementation of
self-hosted WCF HTTP transport and the CLR scheduler it uses. These details are
implementation evidence, not an invitation to depend on private WCF APIs.

## Listener setup is local to each listener

WCF's [SharedHttpTransportManager](https://github.com/microsoft/referencesource/blob/main/System.ServiceModel/System/ServiceModel/Channels/SharedHttpTransportManager.cs)
creates its listener, applies authentication/extended-protection/realm settings,
registers URL prefixes, and runs multiple asynchronous accepts. Its transport
manager tables coordinate WCF endpoints, not arbitrary listeners we create.
WCF also suppresses execution-context flow around accepts.

Consequences for MiniHttpHost:

- WCF binding authentication does not automatically protect our routes. Request
  identity must come from the HTTP context and the application's chosen policy.
- We use a separate listener and request queue; do not obtain or modify WCF's
  listener through reflection. Stopping one should not shut down the other.
- MiniHttpHost suppresses ambient execution-context capture when scheduling its
  long-lived loops. Starting it inside an operation should not retain that
  operation's principal or logical call context. An integration check sets an
  `AsyncLocal` during startup and verifies it isn't inherited by HTTP handlers.

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

The reviewed accept/scheduler paths do not set global thread-pool minimum or
maximum counts. That is a scoped finding, not proof that no component in the
larger application can tune the pool. MiniHttpHost never calls those setters.

WCF also distinguishes pending accepts from service dispatch. Our current fixed
loops couple accept capacity to active handler capacity: long transfers consume
the same budget as ordinary calls. Independent transfer limits or an explicit
bounded admission queue would be further work, not existing behavior.

## Streaming behavior to account for

The framework's [HttpListener request stream](https://github.com/microsoft/referencesource/blob/main/System/net/System/Net/_ListenerRequestStream.cs)
uses a native body-read flag with value 1 outside opaque/WebSocket mode. For
`HttpReceiveRequestEntityBody`, this is the
[fill-buffer behavior](https://learn.microsoft.com/en-us/windows/win32/api/http/nf-http-httpreceiverequestentitybody).
A read can wait for the requested chunk to fill or for the request to end; it
need not return as soon as a smaller amount arrives.

MiniHttpHost's default 4 KiB copy buffer keeps first-chunk latency low, which
measured faster than 80 KiB for downloads and small uploads. Large uploads can pass
`bufferSize: 81920` to `StreamAsync` for fewer I/O calls, and `FileAsync`'s buffer
argument allows the same tuning without private reflection. This remains
bounded streaming, not whole-message buffering. The incremental test sends more
than one copy buffer, then requires response body bytes before sending the rest
of the upload.
Modern .NET may round pooled copy buffers up, so actual read sizes can exceed
the requested buffer size.

The framework's [response stream](https://github.com/microsoft/referencesource/blob/main/System/net/System/Net/_ListenerResponseStream.cs)
implements `Flush`/`FlushAsync` as no-ops. Flushing an application-side buffered
writer can still matter, but flushing the native response stream cannot force a
pending request read to complete.

WCF's [request adapter](https://github.com/microsoft/referencesource/blob/main/System.ServiceModel/System/ServiceModel/Channels/HttpRequestContext.cs)
adds its own stream wrappers and exception translation, including a one-byte
pre-read when the request length is unknown. Such wrappers are framework behavior
above the same listener, not evidence of a separate HTTP server or hidden global
switch we need to copy.
