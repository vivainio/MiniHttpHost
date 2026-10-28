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

Nancy's [self-host implementation](https://github.com/NancyFx/Nancy/blob/master/src/Nancy.Hosting.Self/NancyHost.cs)
creates an HttpListener and accepts requests asynchronously. Nancy itself is
host-independent: the self-host transport isn't a statement about every Nancy
hosting mode. Its routing, request/response adaptation, and other framework
behavior sit above the listener.

Microsoft documents Katana's [HttpListener and SystemWeb hosting modes](https://learn.microsoft.com/en-us/aspnet/aspnet/overview/owin-and-katana/enabling-windows-authentication-in-katana).
The HttpListener host and MiniHttpHost share that transport on Windows; Katana
provides an OWIN pipeline and its hosting abstractions above it. MiniHttpHost
middleware is deliberately small and is not OWIN-compatible middleware.

EmbedIO explicitly offers [two listener modes](https://unosquare.github.io/embedio/api/EmbedIO.HttpListenerMode.html).
Selecting the Microsoft mode uses the runtime's listener. Its internal mode is a
managed implementation; the name "HttpListener" alone doesn't imply HTTP.sys.
See also the project's [overview](https://github.com/unosquare/embedio).

For IIS and ASP.NET Core, see Microsoft's [IIS architecture](https://learn.microsoft.com/en-us/iis/get-started/introduction-to-iis/introduction-to-iis-architecture)
and [server implementations](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/).
Using HTTP.sys doesn't mean a library provides the full IIS hosting environment,
or every feature of ASP.NET Core's separate HTTP.sys server integration.

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

These are architecture comparisons, not benchmarks. A shared underlying listener
doesn't imply equal throughput, buffering, memory use, limits, or shutdown behavior.
Those depend on the framework code above the listener and should be measured with
the application's actual workload.

## Compared with WCF SOAP over HTTP

WCF's [HTTP transport source](https://github.com/microsoft/referencesource/blob/main/System.ServiceModel/System/ServiceModel/Channels/SharedHttpTransportManager.cs)
creates a `System.Net.HttpListener` too. Microsoft's
[WCF HTTP configuration guide](https://learn.microsoft.com/en-us/dotnet/framework/wcf/feature-details/configuring-http-and-https)
describes its HTTP Server API URL registrations, TLS bindings, and coexistence
with other HTTP Server API users. MiniHttpHost's use of HTTP.sys alone is therefore
not a reason to expect it to outperform WCF.

The likely savings come from avoiding SOAP envelopes, XML serialization, and
WCF dispatch for the replacement endpoint. This is an architectural expectation,
not a measured speedup. Cheap calls can expose framework overhead; an operation
dominated by a database or business logic may show little end-to-end improvement.

For binary transfers, compare actual WCF configuration. Text SOAP commonly
encodes binary content as Base64. MTOM can send large binary blocks directly,
and WCF supports streamed transfer modes rather than buffering an entire message.
Replacing buffered text SOAP with raw streaming can therefore improve wire size
and memory use much more than replacing an already-streamed MTOM endpoint.
See Microsoft's [message encoder guidance](https://learn.microsoft.com/en-us/dotnet/framework/wcf/feature-details/choosing-a-message-encoder)
and [large data and streaming documentation](https://learn.microsoft.com/en-us/dotnet/framework/wcf/feature-details/large-data-and-streaming).

A useful Windows comparison would use identical business work, TLS/authentication
requirements, payloads, client reuse, and concurrency. Measure small no-op calls
separately from large uploads/downloads, with warm-up, throughput, p50/p95/p99
latency, server CPU, allocations/GC, and working set. Match WCF throttling against
MiniHttpHost's handler concurrency. Include a mixed workload because both endpoints
will run in the same application and can contend for shared resources.

A first WCF-vs-MiniHttpHost benchmark is in [Benchmarks: MiniHttpHost and WCF](benchmarks.md).
It runs on one Windows machine with the client and both servers in one process, so its
results show loopback behavior rather than server capacity. The integration checks verify
behavior; their runtime is not a throughput benchmark.

## Tuning scope

MiniHttpHost's `concurrency` limits active handlers across the server, including
streaming operations. This is the closest counterpart to WCF's concurrent-call
throttling, rather than a limit on open TCP connections. Each route can set a
body-size limit and execution deadline, and the host chooses a shutdown grace
period. The library does not change the process-wide .NET thread pool.

WCF instance/session throttles have no direct counterpart in this handler model.
Separate upload concurrency, an application queue with an explicit bound/rejection
policy, and HTTP.sys transport-timeout configuration are not currently exposed.
Excess traffic waits in the underlying listener backlog. For a mixed workload,
long transfers consume the same handler budget as ordinary calls; isolate them
with application middleware or a separate server instance when needed.
