# MiniHttpHost

A small embeddable HTTP server with routing, middleware, streaming, and optional
response compression. No runtime NuGet dependencies.

The package contains `net472` and `net10.0` builds. .NET Framework 4.7.2 on Windows
is the intended production target; modern .NET supports development and testing.
Windows uses HTTP.sys, while macOS uses a different managed HttpListener backend.

```csharp
var routes = new HttpRouter()
    .MapGet("/hello", context => context.TextAsync("Hello!"),
        new HttpRouteOptions { EnableCompression = true });
var server = new MiniWebServer("http://localhost:8080/", routes.HandleAsync);
await server.StartAsync();
// Keep the server for your application's lifetime and monitor server.Completion.
await server.StopAsync(TimeSpan.FromSeconds(30));
server.Dispose();
```

See the [book](https://vivainio.github.io/MiniHttpHost/) for complete hosting,
exception handling, streaming, compression, and WCF coexistence examples.
Source: [vivainio/MiniHttpHost](https://github.com/vivainio/MiniHttpHost).
