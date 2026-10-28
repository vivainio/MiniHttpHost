using System;
using System.IO;
using System.IO.Compression;

namespace MiniHttpHost
{
    /// <summary>Providers are shared across requests; create one encoder stream per response.</summary>
    public interface IResponseCompressionProvider
    {
        string EncodingName { get; }
        /// <summary>Disposing the encoder must finish its trailer and leave destination open.</summary>
        Stream CreateCompressionStream(Stream destination);
    }

    public sealed class GzipCompressionProvider : IResponseCompressionProvider
    {
        internal static readonly GzipCompressionProvider Default = new GzipCompressionProvider();
        public GzipCompressionProvider(CompressionLevel level = CompressionLevel.Fastest)
        {
            if (!Enum.IsDefined(typeof(CompressionLevel), level)) throw new ArgumentOutOfRangeException(nameof(level));
            Level = level;
        }

        public CompressionLevel Level { get; }
        public string EncodingName => "gzip";
        public Stream CreateCompressionStream(Stream destination) => new GZipStream(destination, Level, leaveOpen: true);
    }
}
