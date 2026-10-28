using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHttpHost
{
    /// <summary>Exact paths and whole-segment parameters, e.g. /files/{id}.</summary>
    public sealed class HttpRouter
    {
        private readonly object gate = new object();
        private readonly List<Route> routes = new List<Route>();

        public HttpRouter MapGet(string path, HttpHandler handler, HttpRouteOptions options = null)
            => Map("GET", path, handler, options);
        public HttpRouter MapPost(string path, HttpHandler handler, HttpRouteOptions options = null)
            => Map("POST", path, handler, options);

        public HttpRouter Map(string method, string path, HttpHandler handler, HttpRouteOptions options = null)
        {
            if (string.IsNullOrEmpty(method) || method.Any(c => !IsTokenCharacter(c)))
                throw new ArgumentException("An HTTP method token is required.", nameof(method));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var route = new Route(method, path, handler, (options ?? new HttpRouteOptions()).Snapshot());
            lock (gate)
            {
                if (routes.Any(r => r.Method == method && r.Signature == route.Signature))
                    throw new ArgumentException("This method and route pattern are already registered.");
                routes.Add(route);
                routes.Sort(Route.Compare);
            }
            return this;
        }

        public RouteGroup MapGroup(string prefix) => new RouteGroup(this, prefix, new HttpMiddleware[0]);

        public async Task HandleAsync(HttpContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            context.RequestAborted.ThrowIfCancellationRequested();
            Route[] snapshot;
            lock (gate) snapshot = routes.ToArray();
            var allowed = new SortedSet<string>(StringComparer.Ordinal);
            var segments = context.Request.Url.AbsolutePath.Split('/');
            foreach (var route in snapshot)
            {
                if (!route.Matches(segments)) continue;
                allowed.Add(route.Method);
                if (route.Method != context.Request.HttpMethod) continue;
                context.RouteValues.Clear();
                for (int i = 0; i < segments.Length; i++)
                    if (route.Names[i] != null)
                        context.RouteValues.Add(route.Names[i], Uri.UnescapeDataString(segments[i]));
                context.LimitBody(route.Options.MaxRequestBodyBytes);
                if (!route.Options.Timeout.HasValue)
                {
                    await InvokeRouteAsync(context, route).ConfigureAwait(false);
                }
                else
                {
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
                    {
                        context.RequestAborted = deadline.Token;
                        using (deadline.Token.Register(context.Abort))
                        {
                            deadline.CancelAfter(route.Options.Timeout.Value);
                            await InvokeRouteAsync(context, route).ConfigureAwait(false);
                        }
                    }
                }
                return;
            }
            context.Response.StatusCode = allowed.Count == 0 ? 404 : 405;
            if (allowed.Count != 0) context.Response.Headers["Allow"] = string.Join(", ", allowed);
            context.Response.ContentLength64 = 0;
        }

        private static Task InvokeRouteAsync(HttpContext context, Route route)
            => route.Options.EnableCompression
                ? ResponseCompression.RunAsync(context, route.Handler, route.Options.CompressionProvider)
                : route.Handler(context);

        private static bool IsTokenCharacter(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
            (c >= '0' && c <= '9') || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0;

        private sealed class Route
        {
            internal readonly string Method;
            internal readonly string[] Segments;
            internal readonly string[] Names;
            internal readonly string Signature;
            internal readonly HttpHandler Handler;
            internal readonly HttpRouteOptions Options;

            internal Route(string method, string path, HttpHandler handler, HttpRouteOptions options)
            {
                if (string.IsNullOrEmpty(path) || path[0] != '/' || path.IndexOfAny(new[] { '?', '#' }) >= 0)
                    throw new ArgumentException("An absolute path without query or fragment is required.", nameof(path));
                Method = method;
                Handler = handler;
                Options = options;
                Segments = path.Split('/');
                Names = new string[Segments.Length];
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < Segments.Length; i++)
                {
                    string segment = Segments[i];
                    if (segment.StartsWith("{", StringComparison.Ordinal) && segment.EndsWith("}", StringComparison.Ordinal))
                    {
                        string name = segment.Substring(1, segment.Length - 2);
                        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') ||
                            name.Any(c => !(char.IsLetterOrDigit(c) || c == '_')) || !names.Add(name))
                            throw new ArgumentException("Route parameter names must be unique identifiers.", nameof(path));
                        Names[i] = name;
                    }
                    else if (segment.IndexOfAny(new[] { '{', '}', '*' }) >= 0)
                        throw new ArgumentException("Only whole-segment {name} parameters are supported.", nameof(path));
                }
                Signature = string.Join("/", Segments.Select((s, i) => Names[i] == null ? s : "{}"));
            }

            internal bool Matches(string[] path)
            {
                if (path.Length != Segments.Length) return false;
                for (int i = 0; i < path.Length; i++)
                    if (Names[i] == null ? path[i] != Segments[i] : path[i].Length == 0) return false;
                return true;
            }

            internal static int Compare(Route a, Route b)
            {
                int length = a.Names.Length.CompareTo(b.Names.Length);
                if (length != 0) return length;
                for (int i = 0; i < a.Names.Length; i++)
                {
                    int result = (a.Names[i] != null).CompareTo(b.Names[i] != null);
                    if (result != 0) return result; // Earlier literal segments take priority.
                }
                return string.CompareOrdinal(a.Signature, b.Signature);
            }
        }
    }

    public sealed class HttpRouteOptions
    {
        /// <summary>Defaults to 30 MiB. Set null explicitly for no body limit.</summary>
        public long? MaxRequestBodyBytes { get; set; } = 30L * 1024 * 1024;
        /// <summary>Defaults to two minutes. Set null explicitly for no route deadline.</summary>
        public TimeSpan? Timeout { get; set; } = TimeSpan.FromMinutes(2);
        public bool EnableCompression { get; set; }
        public IResponseCompressionProvider CompressionProvider { get; set; } = GzipCompressionProvider.Default;

        internal HttpRouteOptions Snapshot()
        {
            if (MaxRequestBodyBytes < 0) throw new ArgumentOutOfRangeException(nameof(MaxRequestBodyBytes));
            if (Timeout.HasValue && (Timeout.Value <= TimeSpan.Zero || Timeout.Value.TotalMilliseconds > int.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(Timeout));
            if (EnableCompression) ResponseCompression.ValidateProvider(CompressionProvider);
            return new HttpRouteOptions {
                MaxRequestBodyBytes = MaxRequestBodyBytes, Timeout = Timeout,
                EnableCompression = EnableCompression, CompressionProvider = CompressionProvider
            };
        }
    }

    /// <summary>Registers routes with a common prefix and middleware.</summary>
    public sealed class RouteGroup
    {
        private readonly HttpRouter router;
        private readonly string prefix;
        private readonly List<HttpMiddleware> middleware;

        internal RouteGroup(HttpRouter router, string prefix, IEnumerable<HttpMiddleware> inherited)
        {
            if (string.IsNullOrEmpty(prefix) || prefix[0] != '/' || prefix.IndexOfAny(new[] { '?', '#' }) >= 0)
                throw new ArgumentException("An absolute group prefix is required.", nameof(prefix));
            this.router = router;
            this.prefix = prefix.TrimEnd('/');
            middleware = new List<HttpMiddleware>(inherited);
        }

        // Configure groups before registering routes. Middleware is snapshotted at Map.
        public RouteGroup Use(HttpMiddleware component)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            middleware.Add(component);
            return this;
        }

        public RouteGroup MapGroup(string path) => new RouteGroup(router, Combine(path), middleware);
        public RouteGroup MapGet(string path, HttpHandler handler, HttpRouteOptions options = null)
            => Map("GET", path, handler, options);
        public RouteGroup MapPost(string path, HttpHandler handler, HttpRouteOptions options = null)
            => Map("POST", path, handler, options);

        public RouteGroup Map(string method, string path, HttpHandler handler, HttpRouteOptions options = null)
        {
            var pipeline = new HttpPipeline();
            foreach (var component in middleware) pipeline.Use(component);
            router.Map(method, Combine(path), pipeline.Build(handler), options);
            return this;
        }

        private string Combine(string path)
        {
            if (path == null || (path.Length != 0 && path[0] != '/'))
                throw new ArgumentException("A group route must be empty or start with '/'.", nameof(path));
            var combined = prefix + path;
            return combined.Length == 0 ? "/" : combined;
        }
    }
}
