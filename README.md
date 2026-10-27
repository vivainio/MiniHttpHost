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

See Microsoft's [HttpListener documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener?view=netframework-4.7.2)
and [IIS architecture](https://learn.microsoft.com/en-us/iis/get-started/introduction-to-iis/introduction-to-iis-architecture).
ASP.NET Core's default standalone server is Kestrel; the shared HTTP.sys foundation
here applies to Windows HttpListener and IIS hosting. Mono uses a different
implementation. See [the framework comparison](docs/framework-comparison.md).

## Build and embed

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

## Request context and streaming

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
| `StreamAsync(source, contentType, contentLength, leaveOpen: true)` | Incremental copy with an 80 KiB buffer; caller retains stream ownership by default |
| `FileAsync(path, contentType)` | Opens an asynchronous file stream, sends it, and disposes it |

`StreamAsync` infers remaining length from seekable streams. Otherwise, provide a
length or let it use chunked encoding. `FileAsync` sets content length. These two
helpers do not buffer the whole payload. JSON follows the framework serializer's
contracts: use public DTOs or `[DataContract]` / `[DataMember]` types. It is not an
automatic model-binding or arbitrary anonymous-object serialization API.

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

The [console example](examples/ConsoleHost/BinaryEndpoints.cs) registers instance
upload/download handlers. It writes uploads to a temporary file, publishes only
completed uploads, and streams downloads from disk. Its single resource is
serialized so a download never reads a partially uploaded replacement.

## Routing and groups

```csharp
var api = new MyApi(applicationService);
var routes = new HttpRouter();
var group = routes.MapGroup("/api/v1")
    .Use(async (context, next) =>
    {
        if (!authorization.IsAllowed(context.Request))
        {
            await context.TextAsync("Forbidden", 403);
            return;
        }
        context.Items["caller"] = authorization.GetCaller(context.Request);
        await next().ConfigureAwait(false);
    });

group.MapGet("/files/{id}", api.DownloadAsync);
group.MapPost("/files/{id}", api.UploadAsync,
    new HttpRouteOptions {
        MaxRequestBodyBytes = 4L * 1024 * 1024 * 1024,
        Timeout = TimeSpan.FromMinutes(30)
    });
```

`MyApi` and `authorization` are application-owned services. `MapGet`, `MapPost`,
and `Map(method, path, handler)` support literal paths and whole-segment `{name}`
parameters. Groups may be nested. Configure group middleware before mapping its
routes: each registration snapshots the group's pipeline and route options.

Paths are case-sensitive, ignore queries, and distinguish trailing slashes.
They include any listener base path. Parameters are decoded once, after splitting
path segments; empty segments don't match parameters. Literal segments outrank
parameters, comparing from left to right among matching methods. Equivalent
method/pattern registrations are rejected even if parameter names differ.
No regex constraints, optional segments, or catch-all parameters are included.

Use uppercase standard methods. Register HEAD explicitly. Unknown paths return
404; unsupported methods return 405 with `Allow`.

## Middleware, errors, and limits

```csharp
var handler = new HttpPipeline()
    .UseExceptionHandler((context, error) => logger.Log(error))
    .Use(async (context, next) =>
    {
        try { await next().ConfigureAwait(false); }
        catch (System.IO.FileNotFoundException) when (!context.HasStarted)
        {
            // Map a known application error before streaming starts.
            await context.TextAsync("Not found", 404).ConfigureAwait(false);
        }
    })
    .Use(async (context, next) =>
    {
        context.Response.Headers["X-Application"] = "MyApp";
        await next().ConfigureAwait(false);
    })
    .Build(routes.HandleAsync);
```

A then B runs as A → B → route → B → A. `Build` snapshots global middleware;
subsequent registrations do not affect the built handler. Middleware can return
without calling `next` to short-circuit. Await `next` at most once, and do not
retain it after the middleware returns. Request streams are not rewindable and
middleware must not dispose the server-owned bodies.

Errors before a response starts become a generic 500; body-limit failures become
413. Once streaming starts, errors abort the response rather than append an
error page to binary data. The server also has this fallback when no exception
middleware is installed. `UseExceptionHandler` allows an application logging
callback, with `Trace` used by default. Cancellation aborts the connection.

The `!context.HasStarted` filter is essential: a failed partial download must not
be replaced with a 404 body. Such errors reach the outer exception handler, which
logs and aborts the stream. The [console host](examples/ConsoleHost/Program.cs)
also catches startup and listener-lifetime failures at its entry point, reports
them on stderr, and returns a nonzero exit code. Request failures are handled
inside the pipeline so a failed request does not terminate the host. Try
`GET /example-error` to see its generic 500 response and application-side log.

In your application's lifetime coordinator, explicitly observe listener failures:

```csharp
try
{
    // hostStopping is your application's shutdown signal/task.
    await Task.WhenAny(hostStopping, server.Completion).ConfigureAwait(false);
    await server.StopAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
}
catch (Exception error)
{
    logger.Log(error);
    throw; // Let your application decide how to report/recover from a host failure.
}
finally { server.Dispose(); }
```

`MaxRequestBodyBytes` rejects an oversized declared length before invoking the
route. For unknown lengths, the request wrapper counts bytes as they are read.
Limits are enforced through `RequestBody`; there is no automatic whole-body
buffering. Defaults impose no body-size or route-duration limit.

`Timeout` starts after route selection and includes its group middleware. Expiry
signals cancellation and aborts the connection; it does not guarantee a 408
response or forcibly stop application code. It does not configure HTTP.sys
header, connection, or transport timeouts.

## Lifetime and concurrency

`StartAsync` completes when the listener starts. Its token controls startup, not
the server's entire lifetime. `Completion` represents the worker loops' lifetime.

`StopAsync` stops admitting application work, drains active handlers for 30 seconds
by default, and aborts remaining connections/signals cancellation at the deadline.
While draining, newly accepted requests receive 503. Because HttpListener can't
stop accepts separately from active responses, requests still in the underlying
backlog may instead see a closed connection when draining finishes. A handler
that ignores cancellation can outlive `StopAsync`; observe `Completion` for its
eventual exit. `Dispose` aborts immediately. Use a new server instance to restart.

`RunAsync(lifetimeToken)` remains a convenience for running until cancellation;
that cancellation aborts immediately rather than gracefully draining.

The default 16 asynchronous loops bound concurrent handlers, including long
transfers. Excess traffic waits in the underlying listener backlog. Idle accepts
and asynchronous I/O don't reserve a pool thread. Keep handlers asynchronous;
avoid `.Wait()`, `.Result`, and `Thread.Sleep` so they don't starve other work,
including WCF, in the same process.

## Develop and check

```sh
dotnet build examples/ConsoleHost/ConsoleHost.csproj -c Release
mono examples/ConsoleHost/bin/Release/net472/ConsoleHost.exe
curl --data-binary @large.bin http://localhost:8080/binary
curl -o downloaded.bin http://localhost:8080/binary
curl --data-binary @large.bin http://localhost:8080/echo -o echoed.bin
curl http://localhost:8080/api/status
```

Use Ctrl+C to stop the example. On Windows, run the executable directly.

```sh
dotnet build tests/Checks/Checks.csproj -c Release
mono tests/Checks/bin/Release/net472/Checks.exe
```

The checks use local HTTP listeners and cover routing, group authorization,
middleware order, error handling, binary copying, response bytes arriving before
an upload completes, request limits, route timeouts, and graceful/forced shutdown.
The Windows GitHub Actions job runs the same executable on .NET Framework against
HTTP.sys. `MINIHTTPHOST_TEST_PORT` selects the first of two sequential test ports
when explicit Windows URL reservations are needed.

Mono 6.14.1 on macOS supports local development, but is not an HTTP.sys substitute.
An unrestricted chunked request upload previously stalled after the body arrived;
use fixed-length uploads for local Mono development and verify chunked requests
on Windows. Incremental chunked responses and early rejection of oversized chunked
requests pass locally.

The current context-based handler API replaces the initial raw-context/two-argument
API: handlers now receive `HttpContext`, and use `context.RequestAborted` for the
token. Existing application services can stay instance-based.
