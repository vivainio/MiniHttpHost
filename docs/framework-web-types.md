# Useful .NET Framework web types

MiniHttpHost can stay small by exposing streams and letting the application use
framework types directly. These are available to .NET Framework 4.7.2 applications;
add references to the relevant framework assemblies rather than installing another
server framework.

| Task | Existing type or API | What it gives you |
| --- | --- | --- |
| Query parameters | `context.Request.QueryString` | Parsed `NameValueCollection`; use `GetValues` for repeated keys |
| URL construction | `Uri`, `UriBuilder`, `Uri.EscapeDataString` | Parse/build URLs and encode individual path/query values |
| HTML/form encoding | `System.Net.WebUtility` | HTML encoding and form-style URL encoding/decoding |
| Content type parameters | `MediaTypeHeaderValue` | Parse `Content-Type`, charset, and boundary parameters |
| Download filenames | `ContentDispositionHeaderValue` | Format `Content-Disposition` without concatenating raw header text |
| Header syntax | `RangeHeaderValue`, `EntityTagHeaderValue`, `AuthenticationHeaderValue` | Typed parsing/formatting; application implements the actual policy |
| Multipart writing | `MultipartContent`, `MultipartFormDataContent` | Generate MIME boundaries and parts |
| Streaming part content | `StreamContent` | Wrap files or other streams without loading their full contents |
| Small part content | `StringContent`, `ByteArrayContent` | Represent metadata and already-buffered content |
| Outgoing HTTP | `HttpClient`, `HttpRequestMessage`, `HttpResponseMessage` | HTTP requests, explicit headers, streamed responses |
| Cookies | `Cookie`, `CookieCollection`, `CookieContainer` | Server cookie headers and client-side cookie storage |
| Compressed streams | `GZipStream`, `DeflateStream` | Incremental compression/decompression |
| Archive downloads | `ZipArchive` in create mode | Write an archive onto a non-seekable response stream |
| XML | `XmlReader`, `XmlWriter` | Incremental XML processing with explicit reader/writer settings |
| Small JSON DTOs | `DataContractJsonSerializer` | Framework serializer already used by `JsonAsync` |

The HTTP content/header types are in `System.Net.Http`. Compression/archive types
use `System.IO.Compression`. The core MiniHttpHost DLL does not need to wrap each
of these in another public abstraction.

## Read repeated query values

```csharp
string[] tags = context.Request.QueryString.GetValues("tag") ?? new string[0];
string page = context.Request.QueryString["page"];
```

For `?tag=one&tag=two`, `GetValues` retains the two values. Don't parse a query by
splitting on `&` or concatenate unencoded user values into a URL. `UrlEncode` uses
form-style encoding; `Uri.EscapeDataString` is useful for an individual URL value,
not an entire already-assembled URL.

## Use typed headers

```csharp
using System.Net.Http.Headers;

MediaTypeHeaderValue type;
if (MediaTypeHeaderValue.TryParse(context.Request.ContentType, out type))
{
    string mediaType = type.MediaType;
    string charset = type.CharSet;
}

context.Response.Headers["Content-Disposition"] =
    new ContentDispositionHeaderValue("attachment") {
        FileName = "report.bin"
    }.ToString();
```

A header parser checks syntax. `RangeHeaderValue` does not implement a 206 response,
and `EntityTagHeaderValue` does not implement conditional requests. Those need
explicit application behavior; MiniHttpHost currently does not supply range or
cache-validation handlers.

## Reuse an injected HTTP client

```csharp
public sealed class UpstreamApi
{
    private readonly System.Net.Http.HttpClient client;

    public UpstreamApi(System.Net.Http.HttpClient client)
    {
        this.client = client;
    }

    public async System.Threading.Tasks.Task DownloadAsync(
        MiniHttpHost.HttpContext context, System.Uri upstream)
    {
        using (var response = await client.GetAsync(upstream,
            System.Net.Http.HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                await context.StreamAsync(body,
                    "application/octet-stream", response.Content.Headers.ContentLength)
                    .ConfigureAwait(false);
        }
    }
}
```

The application owns the long-lived client; it need not be static. Each operation
disposes its own response. This example streams a body after checking success; it
is not a general reverse proxy. It intentionally doesn't copy arbitrary upstream
headers, statuses, redirects, or authentication policy.

## Know which APIs require a host or package

- `HttpListenerRequest` already supplies headers, cookies, query parameters, and
  the body. Prefer those over creating a `System.Web.HttpContext`.
- `System.Web.HttpRequest.Files` and `HttpPostedFile` belong to the ASP.NET hosting
  pipeline. They are not automatic multipart parsing for a standalone HttpListener.
- `ReadAsMultipartAsync` and `MultipartFileStreamProvider` are supplied by the
  optional **Microsoft.AspNet.WebApi.Client** package, despite their
  `System.Net.Http` namespace. They are not built into the framework assembly.
- ASP.NET Core's `HttpContext`, `IFormFile`, `Results`, and middleware packages are
  a different stack. MiniHttpHost's similarly named context is its own small type.

See [multipart recipes](advanced-http.md) for composing the built-in writer with
an optional incoming-body parser.

Reference: Microsoft's [System.Net.Http](https://learn.microsoft.com/en-us/dotnet/api/system.net.http?view=netframework-4.7.2),
[HTTP header types](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.headers?view=netframework-4.7.2),
[WebUtility](https://learn.microsoft.com/en-us/dotnet/api/system.net.webutility?view=netframework-4.7.2),
and [ZipArchive](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchive?view=netframework-4.7.2)
documentation.
