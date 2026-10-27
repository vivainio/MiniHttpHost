# Multipart requests and responses

MiniHttpHost exposes ordinary request and response streams, so existing .NET HTTP
classes can do the MIME work. The recipes below compose those APIs without adding
packages to the core DLL.

## Send metadata and a binary file together

Use the framework's `System.Net.Http.MultipartContent` for a `multipart/mixed`
response. `StringContent` holds small metadata; `StreamContent` copies the file
incrementally. Add a reference to the framework assembly `System.Net.Http` in the
application using this code.

```csharp
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using MiniHttpHost;

public async Task DownloadBundleAsync(MiniHttpHost.HttpContext context)
{
    using (var bundle = new MultipartContent("mixed"))
    {
        bundle.Add(new StringContent(
            "{\"format\":\"binary\",\"version\":1}",
            Encoding.UTF8, "application/json"));

        var file = new StreamContent(new FileStream(
            "payload.bin", FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, useAsync: true));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") {
            FileName = "payload.bin"
        };
        bundle.Add(file);

        context.RequestAborted.ThrowIfCancellationRequested();
        context.Response.ContentType = bundle.Headers.ContentType.ToString();
        long? length = bundle.Headers.ContentLength;
        if (length.HasValue) context.Response.ContentLength64 = length.Value;
        else context.Response.SendChunked = true;

        // Framework 4.7.2's HttpContent.CopyToAsync has no token overload.
        // Abort the connection if the host or route deadline cancels the request.
        using (context.RequestAborted.Register(context.Abort))
        {
            if (context.Request.HttpMethod != "HEAD")
                await bundle.CopyToAsync(context.ResponseBody).ConfigureAwait(false);
        }
    } // Disposes the parts and the file stream after the send completes.
}
```

Register the instance method normally:

```csharp
routes.MapGet("/bundle", api.DownloadBundleAsync);
```

The writer generates the MIME boundary and part headers. Use its complete
`ContentType` value, including the boundary. Do not call `ReadAsByteArrayAsync`,
`ReadAsStringAsync`, or `LoadIntoBufferAsync` on the complete bundle: those would
buffer the response. Once body writes start, the existing exception handler aborts
failed transfers instead of inserting an error page into binary data.

See Microsoft's [MultipartContent API](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.multipartcontent?view=netframework-4.7.2).

## Upload files and form fields

Clients can send `multipart/form-data` with a mixture of files and metadata:

```sh
curl -F 'description=Example dataset' \
     -F 'files=@large.bin;type=application/octet-stream' \
     -F 'files=@another.bin;type=application/octet-stream' \
     http://localhost:8080/import
```

The framework includes a multipart **writer**, but not the Web API multipart
**reader** extension methods. Incoming MIME parsing is an optional application
integration using Microsoft's `Microsoft.AspNet.WebApi.Client` package, compatible
with .NET Framework 4.7.2:

```xml
<PackageReference Include="Microsoft.AspNet.WebApi.Client" Version="6.0.0" />
```

This package belongs in your host/integration project, not MiniHttpHost itself.
It provides `ReadAsMultipartAsync` and stream providers; using it doesn't require
running an ASP.NET Web API server.

The following handler illustrates a disk-backed inspection/import stage. Every
part goes to a temporary file, including ordinary form fields. Process or move
completed files into application storage before cleanup if they should persist.

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using MiniHttpHost;

public async Task ImportAsync(MiniHttpHost.HttpContext context)
{
    MediaTypeHeaderValue type;
    if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out type) ||
        !string.Equals(type.MediaType, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
    {
        await context.TextAsync("Expected multipart/form-data", 415);
        return;
    }

    string temporary = Path.Combine(Path.GetTempPath(), "minihttp-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    var provider = new MultipartFileStreamProvider(temporary);
    // Borrow the server-owned body. This wrapper acquires no separate I/O resource;
    // don't dispose it and thereby take ownership of the request stream.
    var content = new StreamContent(context.RequestBody);
    content.Headers.ContentType = type;
    if (context.Request.ContentLength64 >= 0)
        content.Headers.ContentLength = context.Request.ContentLength64;

    try
    {
        await content.ReadAsMultipartAsync(provider, 81920, context.RequestAborted)
            .ConfigureAwait(false);

        long bytes = 0;
        foreach (var part in provider.FileData)
        {
            bytes += new FileInfo(part.LocalFileName).Length;
            // Inspect part.Headers.ContentDisposition.Name to identify the field.
            // Import from part.LocalFileName here; it is a server-generated path.
        }
        await context.TextAsync("Received " + provider.FileData.Count + " parts, " + bytes + " bytes");
    }
    finally
    {
        foreach (var part in provider.Contents) part.Dispose();
        Directory.Delete(temporary, recursive: true);
    }
}
```

Apply an overall body limit and a deadline at route registration:

```csharp
routes.MapPost("/import", api.ImportAsync, new HttpRouteOptions {
    MaxRequestBodyBytes = 64L * 1024 * 1024,
    Timeout = TimeSpan.FromMinutes(5)
});
```

This is a composition recipe, not a complete upload policy. For an application
endpoint, also bound the number of parts and metadata-field size. A custom
`MultipartStreamProvider.GetStream` can choose storage and enforce per-part limits
while data arrives. Do not use client-supplied filenames as server paths.

`MultipartFormDataStreamProvider` is convenient when small form fields should be
available in memory; `MultipartFileStreamProvider` above keeps those on disk too.
Avoid the parameterless `ReadAsMultipartAsync()` for large uploads because it uses
a memory-backed provider. Parser I/O errors can wrap underlying exceptions;
preserve request-limit/cancellation semantics when mapping them to HTTP errors,
and do not classify every storage `IOException` as bad client input. In production,
log cleanup failures and arrange removal of leftover temporary files.

See Microsoft's [multipart upload guide](https://learn.microsoft.com/en-us/aspnet/web-api/overview/advanced/sending-html-form-data-part-2)
and the [optional package](https://www.nuget.org/packages/Microsoft.AspNet.WebApi.Client/6.0.0).

## Build multipart requests with HttpClient

A client can use framework classes too:

```csharp
using (var form = new MultipartFormDataContent())
{
    form.Add(new StringContent("Example dataset"), "description");
    form.Add(new StreamContent(File.OpenRead("large.bin")), "files", "large.bin");
    using (var response = await client.PostAsync(importUri, form, cancellationToken))
        response.EnsureSuccessStatusCode();
}
```

Keep `client` as an application-owned, reused `HttpClient`. Disposing the form
closes its file streams. For large downloads, request
`HttpCompletionOption.ResponseHeadersRead`, then copy the response stream instead
of using whole-body convenience methods.

## Other useful streaming formats

- **ZIP bundles:** `System.IO.Compression.ZipArchive` in create mode can write to
  `ResponseBody`. Set `application/zip`, use chunked encoding for an unknown size,
  and construct the archive with `leaveOpen: true`. Dispose the archive before
  returning so it writes its final directory. This is useful for browser downloads.
- **Gzip files:** `GZipStream` can write an `application/gzip` download incrementally.
  Dispose it to finish the trailer and leave the server-owned response open.
  Serving a `.gz` file is distinct from transparent HTTP `Content-Encoding`;
  transparent compression also needs content negotiation and cache handling.
- **NDJSON or progress feeds:** write one serialized record plus a newline at a
  time to `ResponseBody`. Use chunked encoding, observe `RequestAborted`, and avoid
  collecting all records first. A long-lived feed consumes one handler slot.

Multipart is useful when API clients need separate metadata and raw binary parts.
ZIP is often easier when the result should be a single downloadable archive.
Neither automatically adds range requests, resumability, or client-side parsing.
