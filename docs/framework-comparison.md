# HTTP server foundations

MiniHttpHost isn't unique in using the Windows kernel HTTP server. Several
established .NET frameworks use the same transport, depending on their host.

| Framework / hosting mode | HTTP transport | Windows HTTP.sys? |
| --- | --- | --- |
| MiniHttpHost on .NET Framework | `System.Net.HttpListener` | Yes |
| NancyFX `Nancy.Hosting.Self` | `System.Net.HttpListener` | Yes |
| Katana `Microsoft.Owin.Host.HttpListener` | `System.Net.HttpListener` | Yes |
| WCF SOAP over HTTP, self-hosted | `System.Net.HttpListener` in its HTTP transport | Yes |
| EmbedIO, Microsoft listener mode | Runtime-provided `HttpListener` | Yes on Windows |
| EmbedIO, internal listener mode | Its managed listener, derived from Mono/websocket-sharp | No |
| ASP.NET hosted by IIS | IIS's Windows HTTP stack | Yes |
| ASP.NET Core standalone default | Kestrel | No; HTTP.sys is an alternative server |

Sources: [Nancy self-host](https://github.com/NancyFx/Nancy/blob/master/src/Nancy.Hosting.Self/NancyHost.cs),
[Katana hosting](https://learn.microsoft.com/en-us/aspnet/aspnet/overview/owin-and-katana/enabling-windows-authentication-in-katana),
[EmbedIO listener modes](https://unosquare.github.io/embedio/api/EmbedIO.HttpListenerMode.html),
[IIS architecture](https://learn.microsoft.com/en-us/iis/get-started/introduction-to-iis/introduction-to-iis-architecture),
and [ASP.NET Core servers](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/).

The transport does not determine a framework's routing, buffering, or performance.
MiniHttpHost middleware is not OWIN-compatible, and using HTTP.sys does not provide
IIS modules or ASP.NET Core's hosting features.

## What MiniHttpHost borrows

ASP.NET Core inspires the middleware chain, route groups/parameters, per-request
state, response helpers, endpoint limits, and explicit server lifetime. See its
[middleware](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/),
[routing](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/routing/), and
[streaming responses](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/responses)
documentation.

MiniHttpHost's focus is a drop-in .NET Framework 4.7.2 DLL with no runtime NuGet
dependencies. Applications supply their own instance handlers, services, logging,
authorization policy, and storage. It doesn't include MVC, view engines, automatic
model binding, a dependency-injection container, or an alternate HTTP parser.

## Compared with WCF SOAP over HTTP

WCF's [HTTP transport](https://github.com/microsoft/referencesource/blob/main/System.ServiceModel/System/ServiceModel/Channels/SharedHttpTransportManager.cs)
also creates an `HttpListener`. Performance differences depend on the code above
that listener: serialization, dispatch, buffering, and application work.

For binary transfers, compare actual WCF configuration. Text SOAP commonly
encodes binary content as Base64. MTOM can send large binary blocks directly,
and WCF supports streamed transfer modes rather than buffering an entire message.
Replacing buffered text SOAP with raw streaming can therefore improve wire size
and memory use much more than replacing an already-streamed MTOM endpoint.
See Microsoft's [message encoder guidance](https://learn.microsoft.com/en-us/dotnet/framework/wcf/feature-details/choosing-a-message-encoder)
and [large data and streaming documentation](https://learn.microsoft.com/en-us/dotnet/framework/wcf/feature-details/large-data-and-streaming).

See the [benchmark comparison](benchmarks.md) for measured loopback results and
its test conditions. For your application, match business work, payloads,
authentication, and concurrency; measure latency, throughput, CPU, and memory.

## Tuning scope

MiniHttpHost's `concurrency` limits active handlers across the server, including
streaming operations. This is the closest counterpart to WCF's concurrent-call
throttling, rather than a limit on open TCP connections. Each route can set a
body-size limit and execution deadline, and the host chooses a shutdown grace
period. The library does not change the process-wide .NET thread pool.

WCF instance/session throttles have no direct counterpart in this handler model.
Separate upload concurrency and an application queue with an explicit
bound/rejection policy require application code. The `configureListener` callback
can configure supported transport timeouts before startup; see
[listener configuration](security.md#configure-the-listener-before-accepting-requests).
Excess traffic waits in the underlying listener backlog. For a mixed workload,
long transfers consume the same handler budget as ordinary calls; isolate them
with application middleware or a separate server instance when needed.
