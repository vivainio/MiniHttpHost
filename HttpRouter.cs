using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHttpHost
{
    /// <summary>Routes exact, case-sensitive URL paths and HTTP methods.</summary>
    public sealed class HttpRouter
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, Dictionary<string, Func<HttpListenerContext, CancellationToken, Task>>> routes
            = new Dictionary<string, Dictionary<string, Func<HttpListenerContext, CancellationToken, Task>>>(StringComparer.Ordinal);

        public HttpRouter MapGet(string path, Func<HttpListenerContext, CancellationToken, Task> handler)
            => Map("GET", path, handler);

        public HttpRouter MapPost(string path, Func<HttpListenerContext, CancellationToken, Task> handler)
            => Map("POST", path, handler);

        public HttpRouter Map(string method, string path, Func<HttpListenerContext, CancellationToken, Task> handler)
        {
            if (string.IsNullOrWhiteSpace(method) || method.Any(c => !IsTokenCharacter(c)))
                throw new ArgumentException("An HTTP method token is required.", nameof(method));
            if (string.IsNullOrEmpty(path) || path[0] != '/' || path.IndexOfAny(new[] { '?', '#' }) >= 0)
                throw new ArgumentException("An absolute URL path without a query or fragment is required.", nameof(path));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            lock (gate)
            {
                if (!routes.TryGetValue(path, out var methods))
                {
                    methods = new Dictionary<string, Func<HttpListenerContext, CancellationToken, Task>>(StringComparer.Ordinal);
                    routes.Add(path, methods);
                }
                if (methods.ContainsKey(method))
                    throw new ArgumentException("This method and path are already registered.");
                methods.Add(method, handler);
            }
            return this;
        }

        public Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            cancellationToken.ThrowIfCancellationRequested();
            Func<HttpListenerContext, CancellationToken, Task> handler;
            lock (gate)
            {
                if (!routes.TryGetValue(context.Request.Url.AbsolutePath, out var methods))
                {
                    context.Response.StatusCode = 404;
                    context.Response.ContentLength64 = 0;
                    return Task.CompletedTask;
                }
                if (!methods.TryGetValue(context.Request.HttpMethod, out handler))
                {
                    context.Response.StatusCode = 405;
                    context.Response.Headers["Allow"] = string.Join(", ", methods.Keys.OrderBy(m => m, StringComparer.Ordinal));
                    context.Response.ContentLength64 = 0;
                    return Task.CompletedTask;
                }
            }
            // Never invoke application code while holding the route-table lock.
            return handler(context, cancellationToken);
        }

        private static bool IsTokenCharacter(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0;
        }
    }
}
