using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;

namespace RazorLS.Server.Roslyn;

/// <summary>
/// Parses LSP messages from a stream.
/// Optimized to parse JSON directly from bytes and uses ArrayPool to minimize allocations.
/// </summary>
public class LspMessageParser
{
    int _contentLength = -1;

    // "Content-Length:" as bytes for zero-allocation header parsing
    static ReadOnlySpan<byte> ContentLengthHeader => "Content-Length:"u8;
    static ReadOnlySpan<byte> HeaderTerminator => "\r\n\r\n"u8;
    static ReadOnlySpan<byte> LineTerminator => "\r\n"u8;

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
            var span = data.AsSpan(0, dataLength);
            var headerEnd = span.IndexOf(HeaderTerminator);
            if (headerEnd < 0) return false;

            // Parse headers directly from bytes (LSP headers are ASCII)
            var headers = span.Slice(0, headerEnd);
            while (headers.Length > 0)
            {
                var lineEnd = headers.IndexOf(LineTerminator);
                var line = lineEnd < 0 ? headers : headers.Slice(0, lineEnd);

                if (line.StartsWith(ContentLengthHeader))
                {
                    var valueSpan = line.Slice(ContentLengthHeader.Length).Trim((byte)' ');
                    if (Utf8Parser.TryParse(valueSpan, out int contentLength, out _))
                    {
                        _contentLength = contentLength;
                        break;
                    }
                }

                if (lineEnd < 0) break;
                headers = headers.Slice(lineEnd + 2);
            }

            if (_contentLength < 0) return false;

            // Remove headers from buffer
            var contentStart = headerEnd + 4;
            var remainingLength = dataLength - contentStart;
            if (remainingLength > 0)
            {
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
            }
            else
            {
                buffer.SetLength(0);
            }
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
