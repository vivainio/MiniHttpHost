using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHttpHost
{
    public delegate Task HttpHandler(HttpListenerContext context, CancellationToken cancellationToken);

    public delegate Task HttpMiddleware(HttpListenerContext context,
        CancellationToken cancellationToken, Func<Task> next);

    /// <summary>Builds a request pipeline. The first registered middleware runs first.</summary>
    public sealed class HttpPipeline
    {
        private readonly object gate = new object();
        private readonly List<HttpMiddleware> middleware = new List<HttpMiddleware>();

        public HttpPipeline Use(HttpMiddleware component)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            lock (gate) middleware.Add(component);
            return this;
        }

        /// <summary>
        /// Takes a snapshot of registered middleware around a terminal handler.
        /// Later registrations do not change an already-built pipeline.
        /// </summary>
        public Func<HttpListenerContext, CancellationToken, Task> Build(HttpHandler terminal)
        {
            if (terminal == null) throw new ArgumentNullException(nameof(terminal));
            HttpMiddleware[] snapshot;
            lock (gate) snapshot = middleware.ToArray();

            HttpHandler current = terminal;
            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                var component = snapshot[i];
                var downstream = current;
                current = (context, token) => component(context, token,
                    () => downstream(context, token));
            }
            return (context, token) => current(context, token);
        }
    }
}
