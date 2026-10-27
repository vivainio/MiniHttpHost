# Routing and groups

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

