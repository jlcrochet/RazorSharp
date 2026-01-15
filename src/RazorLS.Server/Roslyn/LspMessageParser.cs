using System.Buffers;
using System.Text;
using System.Text.Json;

namespace RazorLS.Server.Roslyn;

/// <summary>
/// Parses LSP messages from a stream.
/// Optimized to parse JSON directly from bytes and uses ArrayPool to minimize allocations.
/// </summary>
public class LspMessageParser
{
    private int _contentLength = -1;

    /// <summary>
    /// Tries to parse a complete LSP message from the buffer.
    /// Returns true if a complete message was parsed, false if more data is needed.
    /// </summary>
    public bool TryParseMessage(MemoryStream buffer, out JsonDocument? message)
    {
        message = null;
        var data = buffer.GetBuffer();
        var dataLength = (int)buffer.Length;

        // If we don't have content length yet, look for headers
        if (_contentLength < 0)
        {
            // Use string-based header parsing (simpler, proven to work)
            var str = Encoding.UTF8.GetString(data, 0, dataLength);
            var headerEnd = str.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) return false;

            var headers = str.Substring(0, headerEnd);
            foreach (var line in headers.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    _contentLength = int.Parse(line.Substring(15).Trim());
                    break;
                }
            }

            if (_contentLength < 0) return false;

            // Remove headers from buffer using pooled array
            var contentStart = headerEnd + 4;
            var remainingLength = dataLength - contentStart;
            var contentBytes = ArrayPool<byte>.Shared.Rent(remainingLength);
            try
            {
                Buffer.BlockCopy(data, contentStart, contentBytes, 0, remainingLength);
                buffer.SetLength(0);
                buffer.Write(contentBytes, 0, remainingLength);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(contentBytes);
            }
            data = buffer.GetBuffer();
            dataLength = remainingLength;
        }

        // Check if we have complete content
        if (dataLength < _contentLength) return false;

        // Parse JSON - must allocate since JsonDocument holds a reference to the backing array
        // and remains valid after this method returns
        var jsonBytes = new byte[_contentLength];
        Buffer.BlockCopy(data, 0, jsonBytes, 0, _contentLength);
        message = JsonDocument.Parse(jsonBytes);

        // Remove processed content from buffer
        var restLength = dataLength - _contentLength;
        if (restLength > 0)
        {
            // Copy remaining data using pooled array
            var remaining = ArrayPool<byte>.Shared.Rent(restLength);
            try
            {
                Buffer.BlockCopy(data, _contentLength, remaining, 0, restLength);
                buffer.SetLength(0);
                buffer.Write(remaining, 0, restLength);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(remaining);
            }
        }
        else
        {
            buffer.SetLength(0);
        }
        _contentLength = -1;

        return true;
    }
}
