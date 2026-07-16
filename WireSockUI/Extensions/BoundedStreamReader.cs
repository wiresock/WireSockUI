using System;
using System.IO;
using System.Text;

namespace WireSockUI.Extensions
{
    internal static class BoundedStreamReader
    {
        internal static string ReadUtf8ToEnd(Stream stream, int maxBytes)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            if (!stream.CanRead) throw new ArgumentException("The response stream is not readable.", nameof(stream));

            if (stream.CanSeek && stream.Length - stream.Position > maxBytes)
                throw new InvalidDataException($"The response exceeds the maximum supported size of {maxBytes} bytes.");

            using (var buffer = new MemoryStream(Math.Min(maxBytes, 8192)))
            {
                var chunk = new byte[Math.Min(maxBytes, 8192)];
                var totalBytes = 0;
                int bytesRead;
                while ((bytesRead = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (bytesRead > maxBytes - totalBytes)
                        throw new InvalidDataException(
                            $"The response exceeds the maximum supported size of {maxBytes} bytes.");

                    buffer.Write(chunk, 0, bytesRead);
                    totalBytes += bytesRead;
                }

                return new UTF8Encoding(false, true).GetString(buffer.ToArray());
            }
        }
    }
}
