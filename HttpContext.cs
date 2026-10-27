using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;

namespace MiniHttpHost
{
    /// <summary>State and streaming bodies belonging to one request.</summary>
    public sealed class HttpContext
    {
        private readonly LimitedReadStream requestBody;

        internal HttpContext(HttpListenerContext native, CancellationToken cancellationToken)
        {
            NativeContext = native;
            RequestAborted = cancellationToken;
            requestBody = new LimitedReadStream(native.Request.InputStream);
            ResponseBody = new StartedWriteStream(native.Response.OutputStream, () => HasStarted = true);
        }

        // Native access is an escape hatch. Use RequestBody/ResponseBody so limits
        // and response-start tracking apply to application reads and writes.
        public HttpListenerContext NativeContext { get; }
        public HttpListenerRequest Request => NativeContext.Request;
        public HttpListenerResponse Response => NativeContext.Response;
        public Stream RequestBody => requestBody;
        public Stream ResponseBody { get; }
        public bool HasStarted { get; private set; }
        public CancellationToken RequestAborted { get; internal set; }
        public IDictionary<string, object> Items { get; } = new Dictionary<string, object>();
        public IDictionary<string, string> RouteValues { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        internal void LimitBody(long? maximumBytes)
        {
            requestBody.MaximumBytes = maximumBytes;
            if (maximumBytes.HasValue &&
                (Request.ContentLength64 > maximumBytes.Value || requestBody.BytesRead > maximumBytes.Value))
                throw new RequestBodyTooLargeException();
        }

        public void Abort()
        {
            try { Response.Abort(); }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
