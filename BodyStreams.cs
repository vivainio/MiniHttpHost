using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHttpHost
{
    public sealed class RequestBodyTooLargeException : IOException
    {
        public RequestBodyTooLargeException() : base("The request body exceeds the configured limit.") { }
    }

    internal abstract class BodyStream : Stream
    {
        protected readonly Stream Inner;
        protected BodyStream(Stream inner) { Inner = inner; }
        public override bool CanRead => Inner.CanRead;
        public override bool CanWrite => Inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => Inner.ReadAsync(buffer, offset, count, token);
        public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => Inner.WriteAsync(buffer, offset, count, token);
        public override void Flush() => Inner.Flush();
        public override Task FlushAsync(CancellationToken token) => Inner.FlushAsync(token);
        // The server owns the underlying streams, not helper wrappers.
    }

    internal sealed class LimitedReadStream : BodyStream
    {
        internal long? MaximumBytes { get; set; }
        internal long BytesRead { get; private set; }
        internal LimitedReadStream(Stream inner) : base(inner) { }

        private int ReadSize(int count)
        {
            if (!MaximumBytes.HasValue || count == 0) return count;
            long remaining = MaximumBytes.Value - BytesRead;
            if (remaining < 0) throw new RequestBodyTooLargeException();
            // Probe one byte past the limit to distinguish EOF at the limit
            // from an oversized chunked/unknown-length request.
            return remaining >= count ? count : (int)remaining + 1;
        }

        private int Account(int count)
        {
            BytesRead += count;
            if (MaximumBytes.HasValue && BytesRead > MaximumBytes.Value)
                throw new RequestBodyTooLargeException();
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Account(Inner.Read(buffer, offset, ReadSize(count)));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => Account(await Inner.ReadAsync(buffer, offset, ReadSize(count), token).ConfigureAwait(false));
    }

    internal sealed class StartedWriteStream : BodyStream
    {
        private readonly Action started;
        internal StartedWriteStream(Stream inner, Action started) : base(inner) { this.started = started; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            started();
            Inner.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            started();
            return Inner.WriteAsync(buffer, offset, count, token);
        }

        public override void Flush() { started(); Inner.Flush(); }
        public override Task FlushAsync(CancellationToken token) { started(); return Inner.FlushAsync(token); }
    }
}
