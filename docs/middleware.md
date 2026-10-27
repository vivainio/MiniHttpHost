# Middleware, errors, and limits

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
logs and aborts the stream. The [console host](https://github.com/vivainio/MiniHttpHost/blob/main/examples/ConsoleHost/Program.cs)
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

