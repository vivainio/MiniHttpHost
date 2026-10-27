# Build and embed

```sh
dotnet build -c Release
```

Reference `bin/Release/net472/MiniHttpHost.dll` and copy it alongside your
application executable, or add a project reference to `MiniHttpHost.csproj`.
The reference-assemblies NuGet package is needed only at build time, including
when building on macOS with the .NET SDK.

Your hosting component owns the server's lifetime:

```csharp
using MiniHttpHost;
using System;
using System.Net;
using System.Threading.Tasks;

// Instance field on your application's hosting component:
private MiniWebServer server;

public async Task StartHttpAsync()
{
    var routes = new HttpRouter()
        .MapGet("/hello/{name}", context =>
            context.TextAsync("Hello, " + context.RouteValues["name"] + "!"))
        .MapPost("/echo", context => context.StreamAsync(context.RequestBody),
            new HttpRouteOptions {
                MaxRequestBodyBytes = 4L * 1024 * 1024 * 1024,
                Timeout = TimeSpan.FromMinutes(30)
            });

    var handler = new HttpPipeline()
        .UseExceptionHandler()
        .Build(routes.HandleAsync);

    server = new MiniWebServer("http://localhost:8080/", handler, concurrency: 16);
    try
    {
        await server.StartAsync(); // Completes when the listener is ready.
    }
    catch (HttpListenerException)
    {
        server.Dispose();
        // Log/report startup failure in your host; don't continue as if listening.
        throw;
    }
    catch
    {
        server.Dispose();
        throw;
    }
}

public async Task StopHttpAsync()
{
    try { await server.StopAsync(TimeSpan.FromSeconds(30)); }
    finally { server.Dispose(); }
}
```

Start and stop once, in order. Monitor `server.Completion` for unexpected listener
failures during the application's lifetime. Handlers can be instance methods or
lambdas with constructor-injected application services; no static request state
is required. All handlers may run concurrently, so synchronize shared state.

On Windows, configure a URL ACL for the actual service account if it lacks
permission to register the prefix. Configure HTTPS certificates through HTTP.sys.
The library doesn't change system URL reservations, TLS bindings, or thread-pool
settings automatically.
