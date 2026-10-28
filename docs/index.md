# MiniHttpHost

A small, embeddable HTTP server targeting .NET Framework 4.7.2 and .NET 10. Add ordinary HTTP
endpoints to an existing application, including a monolith moving away from WCF.
No runtime NuGet dependencies, controller discovery, or DI container required.

Intended for private networks where the existing application and WCF services
already run. MiniHttpHost has not been specifically hardened or validated for
direct public-internet exposure. See the [security analysis](security.md).

On Windows, MiniHttpHost uses `HttpListener` over HTTP.sys, the HTTP stack used
by IIS and self-hosted WCF. It runs inside your application without an IIS
deployment. See [HTTP server foundations](framework-comparison.md) and
[sharing a process with WCF](wcf-coexistence.md) for transport and hosting details.

Start with [Build and embed](getting-started.md), then explore [streaming](streaming.md),
[routing](routing.md), and [middleware and errors](middleware.md).
