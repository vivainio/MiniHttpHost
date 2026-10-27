using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHttpHost
{
    public delegate Task HttpHandler(HttpContext context);

    public delegate Task HttpMiddleware(HttpContext context, Func<Task> next);

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

        /// <summary>Handles errors without replacing a response that already started.</summary>
        public HttpPipeline UseExceptionHandler(Action<HttpContext, Exception> log = null)
        {
            return Use(async (context, next) =>
            {
                try { await next().ConfigureAwait(false); }
                catch (Exception error)
                {
                    await HttpResponses.HandleErrorAsync(context, error, log).ConfigureAwait(false);
                }
            });
        }

        /// <summary>Takes a snapshot; later registrations do not change this handler.</summary>
        public HttpHandler Build(HttpHandler terminal)
        {
            if (terminal == null) throw new ArgumentNullException(nameof(terminal));
            HttpMiddleware[] snapshot;
            lock (gate) snapshot = middleware.ToArray();

            HttpHandler current = terminal;
            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                var component = snapshot[i];
                var downstream = current;
                current = context =>
                {
                    int called = 0;
                    return component(context, () =>
                    {
                        if (Interlocked.Exchange(ref called, 1) != 0)
                            throw new InvalidOperationException("Middleware may call next only once per request.");
                        return downstream(context);
                    });
                };
            }
            return current;
        }
    }
}
