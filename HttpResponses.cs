using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace MiniHttpHost
{
    public static class HttpResponses
    {
        public static Task TextAsync(this HttpContext context, string text, int statusCode = 200)
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? "");
            Prepare(context, "text/plain; charset=utf-8", bytes.LongLength, statusCode);
            return IsHead(context) ? Task.CompletedTask :
                context.ResponseBody.WriteAsync(bytes, 0, bytes.Length, context.RequestAborted);
        }

        /// <summary>Serializes small JSON responses using the framework serializer.</summary>
        public static async Task JsonAsync<T>(this HttpContext context, T value, int statusCode = 200)
        {
            // JSON convenience buffers serialization. Large binary payloads use
            // StreamAsync/FileAsync, which do not buffer the complete response.
            using (var buffer = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(buffer, value);
                buffer.Position = 0;
                await context.StreamAsync(buffer, "application/json; charset=utf-8", buffer.Length,
                    statusCode: statusCode).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Copies incrementally. By default the caller retains ownership of source.
        /// A non-seekable source without a supplied length uses chunked encoding.
        /// </summary>
        public static async Task StreamAsync(this HttpContext context, Stream source,
            string contentType = "application/octet-stream", long? contentLength = null,
            bool leaveOpen = true, int statusCode = 200)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            try
            {
                if (!contentLength.HasValue && source.CanSeek)
                    contentLength = source.Length - source.Position;
                Prepare(context, contentType, contentLength, statusCode);
                if (!IsHead(context))
                    await source.CopyToAsync(context.ResponseBody, 81920, context.RequestAborted).ConfigureAwait(false);
            }
            finally { if (!leaveOpen) source.Dispose(); }
        }

        public static async Task FileAsync(this HttpContext context, string path,
            string contentType = "application/octet-stream")
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, useAsync: true))
                await context.StreamAsync(input, contentType, input.Length).ConfigureAwait(false);
        }

        internal static async Task HandleErrorAsync(HttpContext context, Exception error,
            Action<HttpContext, Exception> log = null)
        {
            if (context.RequestAborted.IsCancellationRequested)
            {
                context.Abort();
                return;
            }
            if (log != null) log(context, error);
            else Trace.TraceError("HTTP request failed: {0}", error);
            if (context.HasStarted)
            {
                context.Abort();
                return;
            }
            int status = error is RequestBodyTooLargeException ? 413 : 500;
            context.Response.KeepAlive = false;
            await context.TextAsync(status == 413 ? "Request body too large\n" : "Internal server error\n", status)
                .ConfigureAwait(false);
        }

        private static bool IsHead(HttpContext context) => context.Request.HttpMethod == "HEAD";

        private static void Prepare(HttpContext context, string contentType, long? length, int status)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.HasStarted) throw new InvalidOperationException("The response has already started.");
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            context.RequestAborted.ThrowIfCancellationRequested();
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            context.Response.SendChunked = !length.HasValue;
            if (length.HasValue) context.Response.ContentLength64 = length.Value;
        }
    }
}
