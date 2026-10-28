using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHttpHost
{
    internal static class ResponseCompression
    {
        internal static void ValidateProvider(IResponseCompressionProvider provider)
        {
            StringWithQualityHeaderValue token;
            if (provider == null || !StringWithQualityHeaderValue.TryParse(provider.EncodingName, out token) ||
                token.Quality.HasValue || token.Value != provider.EncodingName || token.Value == "*" ||
                token.Value.Equals("identity", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A compression provider must declare a content-coding token.", nameof(provider));
        }

        internal static async Task RunAsync(HttpContext context, HttpHandler next, IResponseCompressionProvider provider)
        {
            AddVary(context);
            double? coding = null, wildcard = null, identity = null;
            foreach (string item in (context.Request.Headers["Accept-Encoding"] ?? "").Split(','))
            {
                StringWithQualityHeaderValue value;
                if (!StringWithQualityHeaderValue.TryParse(item.Trim(), out value)) continue;
                double quality = value.Quality ?? 1;
                if (value.Value.Equals(provider.EncodingName, StringComparison.OrdinalIgnoreCase)) coding = Math.Min(coding ?? 1, quality);
                else if (value.Value == "*") wildcard = Math.Min(wildcard ?? 1, quality);
                else if (value.Value.Equals("identity", StringComparison.OrdinalIgnoreCase)) identity = Math.Min(identity ?? 1, quality);
            }
            double codingQuality = coding ?? wildcard ?? 0;
            bool identityAllowed = (identity ?? (wildcard == 0 ? 0 : 1)) > 0;
            bool acceptCoding = codingQuality > 0 && (!identity.HasValue || identity.Value <= codingQuality);
            if (!acceptCoding && !identityAllowed)
            {
                NotAcceptable(context);
                return;
            }

            var original = context.ResponseBody;
            var output = new CompressionStream(context, original, acceptCoding, identityAllowed, provider);
            context.ResponseBody = output;
            try
            {
                await next(context).ConfigureAwait(false);
                output.Finish();
                if (!context.HasStarted) AddVary(context);
            }
            catch (EncodingNotAcceptableException) when (!context.HasStarted)
            {
                output.Fail();
                NotAcceptable(context);
            }
            catch
            {
                output.Fail();
                if (context.HasStarted) context.Abort();
                throw;
            }
            finally { context.ResponseBody = original; }
        }

        private static void NotAcceptable(HttpContext context)
        {
            context.Response.StatusCode = 406;
            context.Response.SendChunked = false;
            context.Response.ContentLength64 = 0;
            context.Response.Headers.Remove("Content-Encoding");
        }

        private static void AddVary(HttpContext context)
        {
            string existing = context.Response.Headers["Vary"];
            if (ContainsToken(existing, "*") || ContainsToken(existing, "Accept-Encoding")) return;
            context.Response.Headers["Vary"] = string.IsNullOrWhiteSpace(existing) ? "Accept-Encoding" : existing + ", Accept-Encoding";
        }

        private static bool ContainsToken(string header, string token)
        {
            ReadOnlySpan<char> remaining = header.AsSpan();
            while (true)
            {
                int comma = remaining.IndexOf(',');
                var item = comma < 0 ? remaining : remaining.Slice(0, comma);
                if (item.Trim().Equals(token.AsSpan(), StringComparison.OrdinalIgnoreCase)) return true;
                if (comma < 0) return false;
                remaining = remaining.Slice(comma + 1);
            }
        }

        private sealed class EncodingNotAcceptableException : Exception { }

        private sealed class CompressionStream : BodyStream
        {
            private readonly HttpContext context;
            private readonly bool acceptCoding;
            private readonly bool identityAllowed;
            private readonly IResponseCompressionProvider provider;
            private readonly GuardedOutput output;
            private Stream encoder;
            private bool decided;
            private bool changedHeaders;
            private bool originalChunked;

            internal CompressionStream(HttpContext context, Stream inner, bool acceptCoding, bool identityAllowed, IResponseCompressionProvider provider)
                : base(inner)
            {
                this.context = context;
                this.acceptCoding = acceptCoding;
                this.identityAllowed = identityAllowed;
                this.provider = provider;
                output = new GuardedOutput(inner);
            }

            private Stream Select()
            {
                context.RequestAborted.ThrowIfCancellationRequested();
                if (decided) return encoder ?? Inner;
                decided = true;
                AddVary(context);
                int status = context.Response.StatusCode;
                bool bodyAllowed = context.Request.HttpMethod != "HEAD" && status >= 200 && status != 204 && status != 205 && status != 304;
                bool eligible = bodyAllowed && status != 206 &&
                    string.IsNullOrEmpty(context.Request.Headers["Range"]) &&
                    string.IsNullOrEmpty(context.Response.Headers["Content-Range"]) &&
                    string.IsNullOrEmpty(context.Response.Headers["Content-Encoding"]) &&
                    string.IsNullOrEmpty(context.Response.Headers["ETag"]) &&
                    string.IsNullOrEmpty(context.Response.Headers["Content-MD5"]) &&
                    string.IsNullOrEmpty(context.Response.Headers["Content-Digest"]) &&
                    string.IsNullOrEmpty(context.Response.Headers["Digest"]) &&
                    string.IsNullOrEmpty(context.Response.Headers["Repr-Digest"]) &&
                    !ContainsToken(context.Response.Headers["Cache-Control"], "no-transform") &&
                    Compressible(context.Response.ContentType) &&
                    (context.Response.SendChunked || context.Response.ContentLength64 != 0);
                if (acceptCoding && eligible)
                {
                    originalChunked = context.Response.SendChunked;
                    context.Response.SendChunked = true; // Never send the uncompressed length.
                    context.Response.Headers["Content-Encoding"] = provider.EncodingName;
                    changedHeaders = true;
                    encoder = provider.CreateCompressionStream(output) ?? throw new InvalidOperationException("The compression provider returned no stream.");
                }
                else if (bodyAllowed && !identityAllowed && string.IsNullOrEmpty(context.Response.Headers["Content-Encoding"]))
                    throw new EncodingNotAcceptableException();
                return encoder ?? Inner;
            }

            private static bool Compressible(string contentType)
            {
                ReadOnlySpan<char> type = contentType.AsSpan();
                int separator = type.IndexOf(';');
                if (separator >= 0) type = type.Slice(0, separator);
                type = type.Trim();
                if (type.Length == 0 || type.Equals("text/event-stream".AsSpan(), StringComparison.OrdinalIgnoreCase)) return false;
                if (type.StartsWith("image/".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                    !type.Equals("image/svg+xml".AsSpan(), StringComparison.OrdinalIgnoreCase)) return false;
                return !type.StartsWith("audio/".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                    !type.StartsWith("video/".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                    !type.Equals("application/zip".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                    !type.Equals("application/gzip".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                    !type.Equals("application/x-gzip".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                    !type.Equals("application/x-7z-compressed".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                    !type.Equals("application/x-rar-compressed".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                    !type.Equals("application/pdf".AsSpan(), StringComparison.OrdinalIgnoreCase);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                var stream = Select();
                context.MarkResponseStarted();
                stream.Write(buffer, offset, count);
            }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                var stream = Select();
                context.MarkResponseStarted();
                return stream.WriteAsync(buffer, offset, count, token);
            }
            public override void Flush() { var stream = Select(); context.MarkResponseStarted(); stream.Flush(); }
            public override Task FlushAsync(CancellationToken token)
            {
                var stream = Select();
                context.MarkResponseStarted();
                return stream.FlushAsync(token);
            }

            internal void Finish()
            {
                context.RequestAborted.ThrowIfCancellationRequested();
                encoder?.Dispose(); // Finish the trailer without closing the native body.
                encoder = null;
            }

            internal void Fail()
            {
                output.Discard = true; // Cleanup must not append a valid trailer to a failed transfer.
                try { encoder?.Dispose(); }
                catch (Exception error) { Trace.TraceError("Compression cleanup failed: {0}", error); }
                finally
                {
                    encoder = null;
                    if (changedHeaders && !context.HasStarted)
                    {
                        context.Response.Headers.Remove("Content-Encoding");
                        context.Response.SendChunked = originalChunked;
                    }
                }
            }
        }

        private sealed class GuardedOutput : BodyStream
        {
            internal bool Discard;
            internal GuardedOutput(Stream inner) : base(inner) { }
            public override void Write(byte[] buffer, int offset, int count) { if (!Discard) Inner.Write(buffer, offset, count); }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
                => Discard ? Task.CompletedTask : Inner.WriteAsync(buffer, offset, count, token);
            public override void Flush() { if (!Discard) Inner.Flush(); }
            public override Task FlushAsync(CancellationToken token) => Discard ? Task.CompletedTask : Inner.FlushAsync(token);
        }
    }
}
