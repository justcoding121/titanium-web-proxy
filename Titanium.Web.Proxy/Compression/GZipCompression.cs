﻿using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Compression
{
    /// <summary>
    /// concreate implementation of gzip compression
    /// </summary>
    internal class GZipCompression : ICompression
    {
        public Stream GetStream(Stream stream)
        {
            return new GZipStream(stream, CompressionMode.Compress, true);
        }
    }
}
