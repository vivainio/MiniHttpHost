# Security analysis

MiniHttpHost is intended for private-network deployments alongside existing
applications. It has not been hardened or validated for direct public-internet
exposure. Private networks still require authentication, authorization, and
request limits; public-facing deployments need a separate security review.

On Windows, HTTP.sys handles HTTP framing, connections, and HTTPS transport.
Application handlers run with the embedding process's permissions. Mono and
modern .NET on other operating systems use different listener implementations.

## Where the boundaries lie

| Layer | Protection or responsibility |
| --- | --- |
| Windows HTTP.sys | HTTP transport, request parsing and routing, kernel request queues, configured transport limits and HTTPS certificate bindings |
| .NET HttpListener | Managed request/response API and configured listener authentication |
| MiniHttpHost | Routing, bounded active handler count, configurable body limits and route deadlines, streaming, generic error responses |
| Host application | Authentication configuration, authorization, input validation, safe file/storage access, browser policies, logging and resource budgets |
| Deployment | OS/runtime updates, certificate lifecycle, URL reservations, firewall rules, service-account privileges and any reverse proxy |

See Microsoft's [HttpListener documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener?view=netframework-4.7.2)
for the Windows transport. IIS modules and request-filtering policies do not apply
to this standalone listener.

## Configure the listener before accepting requests

`MiniWebServer` accepts a `configureListener` callback, invoked before the URL
prefix is registered and the listener starts. For a Windows deployment using
integrated authentication:

```csharp
var server = new MiniWebServer(
    "https://api.example.com:8443/",
    routes.HandleAsync,
    concurrency: 16,
    configureListener: listener =>
    {
        listener.AuthenticationSchemes = System.Net.AuthenticationSchemes.Negotiate;
    });
```

The callback can also configure supported `HttpListener.TimeoutManager` settings.
These are transport settings, distinct from route deadlines. Support varies by
platform; configure and verify them on the deployment OS. Do not start, close or
retain the listener in the callback. A configuration exception fails startup.

Without authentication configuration, the listener uses HttpListener's anonymous
access default. Selecting Negotiate authenticates callers; handlers or middleware
must still authorize each operation and each requested object. Use the request's
`NativeContext.User` for the authenticated principal rather than assuming an
ambient thread principal represents the caller. Existing WCF binding security
is independent of this listener, even in the same process.

Applications using tokens can validate them in middleware instead. Do not trust
an identity header supplied directly by the client. If a proxy supplies identity,
restrict backend access to that proxy and have it remove and replace client-sent
identity headers. Forwarded host, scheme and client-address headers likewise need
an explicit trusted-proxy policy.

## Exposure, TLS and process privileges

Use an explicit hostname in the prefix. Microsoft warns against top-level `*`
and `+` wildcard bindings in the
[HttpListener guidance](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener?view=netframework-4.7.2).
A hostname selects a URL namespace; it does not authenticate a caller or replace
firewall rules. Restrict network reachability to the intended clients or proxy.
Local-only endpoints still need an authorization policy for sensitive operations.

A URL ACL grants a local account permission to register a URL namespace; it does
not authorize remote clients. Reserve only the required namespace for the service
account and run the application with the minimum permissions it needs. See
[namespace reservations](https://learn.microsoft.com/en-us/windows/win32/http/namespace-reservations-registrations-and-routing).

For direct HTTPS hosting, configure the HTTP.sys certificate binding and maintain
certificate renewal and Windows TLS policy. An `https://` prefix alone does not
install a certificate. Windows exposes URL ACLs, certificate bindings and transport
timeouts through [netsh http](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-http).
Coordinate changes with other services using HTTP.sys on the machine.

Separate ports give WCF and MiniHttpHost independent listeners, not process
isolation. A compromised handler has the embedding application's permissions;
CPU, memory, disk exhaustion or a process crash can affect both services. Use a
separate process and restricted identity when that isolation is required.

## Resource exhaustion and request limits

The `concurrency` argument bounds active handlers, including long transfers. It
does not bound all TCP connections or all requests waiting in HTTP.sys, provide
per-client fairness, or reserve capacity for existing WCF operations. Streaming
avoids whole-body buffering only while the handler and its parsers also stream.

Routes default to a 30 MiB request-body limit and a two-minute route deadline.
These defaults also apply when an options object only configures another feature.
Set `MaxRequestBodyBytes` and `Timeout` in `HttpRouteOptions` for the endpoint's
actual workload; see [limits and cancellation](middleware.md). Body enforcement
counts reads through `context.RequestBody`. Reading `Request.InputStream` directly
bypasses it. Global middleware runs before route limits are applied, so it should
not read unbounded bodies. Unknown-length bodies are limited as they are read,
not by scanning the entire body in advance.

A route deadline starts after selection, includes group middleware, signals
cancellation and aborts the connection. It cannot kill application code that
ignores cancellation or roll back side effects. Use cancellation-aware storage
and downstream calls with their own timeouts. Header arrival, idle connections,
and requests waiting for a handler need transport/deployment controls.

HTTP.sys has separate request-line and header limits; these are not upload-body
limits. Review the documented
[HTTP.sys settings](https://learn.microsoft.com/en-us/troubleshoot/developer/webapps/iis/iisadmin-service-inetinfo/httpsys-registry-windows)
for the deployed Windows version rather than assuming an IIS or ASP.NET Core
default applies to this listener. MiniHttpHost does not change machine-wide
registry settings. Add rate limits and admission controls at an appropriate
proxy or in the application when exposed to untrusted workloads.

## Handler security remains application code

- Authorize access to each resource, including downloads. A matched route is not
  an access decision. Configure protected group middleware before mapping routes.
- Treat route values, queries, headers and uploaded filenames as untrusted input.
  Prefer an application identifier mapped to a server-owned file path. `FileAsync`
  opens the supplied path; it does not sandbox access to a directory.
- Bound parser depth, multipart part counts, temporary storage and decompressed
  sizes as appropriate. An upload-byte limit does not bound expanded archives or
  expensive application processing. Use safe XML settings for any SOAP adapter.
- Browser-facing applications need context-appropriate output encoding, cookie
  policy and CSRF protection for cookie-authenticated state changes. CORS is not
  authorization. Configure response headers and cache policy for the data served.
- Keep response compression disabled for responses that mix secrets with
  attacker-controlled reflected content when compression side channels are a
  concern. Compression is opt-in; see [response compression](compression.md).

Failures before response bytes are written produce generic 500 responses, or 413
for recognized body-limit failures. After streaming starts, failures abort the
response. Exception details go to application logging or `Trace`, so protect and
redact those logs. Do not log credentials or unrestricted request bodies.

## Verification before deployment

The existing checks exercise declared and chunked body limits, route cancellation,
generic error bodies, streaming failure handling and shutdown. They do not prove
resistance to hostile traffic or validate a deployment's TLS and identity policy.

On the target Windows environment, verify that unauthenticated and unauthorized
callers are rejected, oversized and slow requests release capacity, certificates
validate, and only intended network paths reach the listener. Measure effects on
the existing application under sustained load. Keep Windows and the .NET runtime
serviced: delegating transport to the platform also makes its security updates
part of operating the endpoint.
