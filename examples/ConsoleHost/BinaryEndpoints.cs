using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHttpHost.Examples
{
    // A single file-backed binary resource, with bounded transfer memory.
    // Serialize access so a download never reads a file being replaced.
    internal sealed class BinaryEndpoints
    {
        private const int BufferSize = 81920;
        private readonly string path;
        private readonly SemaphoreSlim access = new SemaphoreSlim(1, 1);

        public BinaryEndpoints(string directory)
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "payload.bin");
        }

        public async Task UploadAsync(HttpListenerContext context, CancellationToken token)
        {
            await access.WaitAsync(token).ConfigureAwait(false);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    await context.Request.InputStream.CopyToAsync(output, BufferSize, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                // Publish only a completed upload; preserve the old file on a
                // failed or cancelled transfer. Temp file is on the same volume.
                if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                else File.Move(temporaryPath, path);
                context.Response.StatusCode = 204;
                context.Response.ContentLength64 = 0;
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                finally { access.Release(); }
            }
        }

        public async Task DownloadAsync(HttpListenerContext context, CancellationToken token)
        {
            await access.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!File.Exists(path))
                {
                    context.Response.StatusCode = 404;
                    context.Response.ContentLength64 = 0;
                    return;
                }
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, BufferSize, useAsync: true))
                {
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.ContentLength64 = input.Length;
                    await input.CopyToAsync(context.Response.OutputStream, BufferSize, token).ConfigureAwait(false);
                }
            }
            finally { access.Release(); }
        }
    }
}
