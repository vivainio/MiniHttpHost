# MiniHttpHost

A small, embeddable HTTP server targeting .NET Framework 4.7.2 and .NET 10. Add ordinary HTTP
endpoints to an existing application, including a monolith moving away from WCF.
No runtime NuGet dependencies, controller discovery, or DI container required.

Intended for private networks where the existing application and WCF services
already run. MiniHttpHost has not been specifically hardened or validated for
direct public-internet exposure. See the [security analysis](security.md).

**On Windows, MiniHttpHost uses HTTP.sys, the kernel-mode HTTP server stack also
used by IIS to host ASP.NET applications.** `HttpListener` exposes that Windows
infrastructure directly, so the library lives inside your existing process
without requiring an IIS deployment. Handlers run on the shared managed thread
pool; there are no dedicated accept threads or custom worker pools.

**Self-hosted WCF over HTTP uses the same `System.Net.HttpListener`.** Microsoft's
[WCF HTTP transport source](https://github.com/microsoft/referencesource/blob/main/System.ServiceModel/System/ServiceModel/Channels/SharedHttpTransportManager.cs)
creates it directly. MiniHttpHost simplifies the layers above the listener by
using ordinary HTTP handlers and streams in place of WCF's SOAP processing and
dispatch. Sharing HTTP.sys alone doesn't establish a performance advantage over WCF.
See the [WCF coexistence review](wcf-coexistence.md) for running both in one process on separate ports,
with independent listeners and shared CLR capacity.

See Microsoft's [HttpListener documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener?view=netframework-4.7.2)
and [IIS architecture](https://learn.microsoft.com/en-us/iis/get-started/introduction-to-iis/introduction-to-iis-architecture).
ASP.NET Core's default standalone server is Kestrel; the shared HTTP.sys foundation
here applies to Windows HttpListener and IIS hosting. Mono uses a different
implementation. See [the framework comparison](framework-comparison.md).


Start with [Build and embed](getting-started.md), then explore [streaming](streaming.md),
[routing](routing.md), and [middleware and errors](middleware.md).
