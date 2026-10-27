# MiniHttpHost

A small, embeddable HTTP server for .NET Framework 4.7.2. Add ordinary HTTP
endpoints to an existing application, including a monolith moving away from WCF.
No runtime NuGet dependencies, controller discovery, or DI container required.

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
See the [WCF coexistence review](docs/wcf-coexistence.md) for listener settings,
URL wildcard precedence, internal scheduling, and shared-process implications.

See Microsoft's [HttpListener documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener?view=netframework-4.7.2)
and [IIS architecture](https://learn.microsoft.com/en-us/iis/get-started/introduction-to-iis/introduction-to-iis-architecture).
ASP.NET Core's default standalone server is Kestrel; the shared HTTP.sys foundation
here applies to Windows HttpListener and IIS hosting. Mono uses a different
implementation. See [the framework comparison](docs/framework-comparison.md).

## Documentation

Read **[The MiniHttpHost book](https://vivainio.github.io/MiniHttpHost/)** for the full guide,
or browse the chapters on GitHub:

- [Build and embed](docs/getting-started.md)
- [Request context and streaming](docs/streaming.md)
- [Routing and groups](docs/routing.md)
- [Middleware, exception handling, and limits](docs/middleware.md)
- [Lifetime and concurrency](docs/lifetime.md)
- [WCF coexistence](docs/wcf-coexistence.md)
- [Framework and performance comparison](docs/framework-comparison.md)
- [Examples and checks](docs/development.md)

## Build

```sh
dotnet build -c Release
```

Reference `bin/Release/net472/MiniHttpHost.dll` in your application. There are no
runtime NuGet dependencies. Handlers are ordinary instance methods or lambdas;
the [embedding example](docs/getting-started.md) shows startup, shutdown, and error
handling.

## Try the example

```sh
dotnet build examples/ConsoleHost/ConsoleHost.csproj -c Release
mono examples/ConsoleHost/bin/Release/net472/ConsoleHost.exe
curl http://localhost:8080/hello/world
```

On Windows, run `ConsoleHost.exe` directly. For local macOS development, use Mono.
See [development notes](docs/development.md) for platform differences and checks.

The documentation is built and deployed by GitHub Actions. See
[Building this book](docs/documentation.md) for local preview and validation.
