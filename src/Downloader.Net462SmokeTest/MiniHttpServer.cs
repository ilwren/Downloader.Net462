using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Net462SmokeTest;

/// <summary>
/// A minimal single-purpose HTTP/1.1 server for the net462 smoke run.
///
/// Implemented directly on <see cref="TcpListener"/> rather than <see cref="HttpListener"/>:
/// an <see cref="HttpListener"/> requires a URL ACL reservation (netsh http add urlacl) or an
/// elevated process on Windows, which would make the CI leg fragile; a raw TCP listener needs
/// neither. It implements exactly the surface the downloader exercises: GET with optional
/// <c>Range</c> (mirroring the probe <c>Range: bytes=0-0</c> and real chunk ranges),
/// <c>206 Partial Content</c> with <c>Content-Range</c>, <c>Accept-Ranges</c>,
/// <c>Content-Disposition</c> (for file-name resolution) and a slow drip-fed body for
/// pause/cancel scenarios. One request per connection (<c>Connection: close</c>).
/// </summary>
internal sealed class MiniHttpServer : IDisposable
{
    public const int FileSize = 256 * 1024;       // /file.bin    — range-capable, small
    public const int BigFileSize = 3 * 1024 * 1024; // /big.bin   — range-capable, drip-fed (~3s)
    public const int NoRangeSize = 64 * 1024;      // /norange.bin — always 200, no Accept-Ranges

    private const int DripBlockSize = 16 * 1024;
    private const int DripDelayMs = 15;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;

    public int Port { get; }

    public string BaseUrl => "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);

    public MiniHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoop);
    }

    /// <summary>
    /// Deterministic byte pattern used for both serving and verification. Same contents the
    /// test side reconstructs, so a downloaded file can be compared byte-for-byte.
    /// </summary>
    internal static byte[] PatternBytes(int size)
    {
        byte[] data = new byte[size];
        for (int i = 0; i < size; i++)
            data[i] = unchecked((byte)((i * 31 + (i >> 8)) & 0xFF));
        return data;
    }

    internal static bool MatchesPattern(byte[] data, int size)
    {
        if (data == null || data.Length != size)
            return false;

        for (int i = 0; i < size; i++)
        {
            if (data[i] != unchecked((byte)((i * 31 + (i >> 8)) & 0xFF)))
                return false;
        }

        return true;
    }

    private async Task AcceptLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break; // listener stopped
            }
            catch (SocketException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(() => HandleClient(client));
        }
    }

    private static async Task HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                string head = await ReadRequestHead(stream).ConfigureAwait(false);
                if (string.IsNullOrEmpty(head))
                    return;

                string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                string[] requestLine = lines[0].Split(' ');
                if (requestLine.Length < 2 ||
                    !"GET".Equals(requestLine[0], StringComparison.OrdinalIgnoreCase))
                {
                    await WriteTextResponse(stream, 400, "Bad Request").ConfigureAwait(false);
                    return;
                }

                string path = requestLine[1];
                long? rangeStart = null;
                long? rangeEnd = null;
                foreach (string headerLine in lines.Skip(1))
                {
                    if (headerLine.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseRangeHeader(headerLine.Substring("Range:".Length), out rangeStart, out rangeEnd);
                    }
                }

                switch (path)
                {
                    case "/file.bin":
                        await SendFile(stream, "file.bin", FileSize, rangeStart, rangeEnd,
                            drip: false, acceptRanges: true).ConfigureAwait(false);
                        break;
                    case "/big.bin":
                        await SendFile(stream, "big.bin", BigFileSize, rangeStart, rangeEnd,
                            drip: true, acceptRanges: true).ConfigureAwait(false);
                        break;
                    case "/norange.bin":
                        // A server that ignores the Range header entirely and never advertises
                        // range support — exercises the single-connection fallback path.
                        await SendFile(stream, "norange.bin", NoRangeSize, null, null,
                            drip: false, acceptRanges: false).ConfigureAwait(false);
                        break;
                    default:
                        await WriteTextResponse(stream, 404, "Not Found: " + path).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception)
            {
                // Client aborted mid-transfer (expected: the cancel smoke test drops the
                // connection) or any other per-connection failure. Never escapes to the host.
            }
        }
    }

    private static void ParseRangeHeader(string value, out long? start, out long? end)
    {
        start = null;
        end = null;
        value = value.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return;

        string spec = value.Substring("bytes=".Length);
        int dash = spec.IndexOf('-');
        if (dash < 0)
            return;

        long parsed;
        string startText = spec.Substring(0, dash).Trim();
        string endText = spec.Substring(dash + 1).Trim();
        if (startText.Length > 0 && long.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            start = parsed;
        if (endText.Length > 0 && long.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            end = parsed;
    }

    private static async Task<string> ReadRequestHead(NetworkStream stream)
    {
        byte[] buffer = new byte[16 * 1024];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer, total, buffer.Length - total).ConfigureAwait(false);
            if (read == 0)
                return total == 0 ? null : string.Empty; // client closed without a request

            total += read;
            if (total >= 4 &&
                buffer[total - 4] == '\r' && buffer[total - 3] == '\n' &&
                buffer[total - 2] == '\r' && buffer[total - 1] == '\n')
                break;
        }

        return Encoding.ASCII.GetString(buffer, 0, total);
    }

    private static async Task SendFile(NetworkStream stream, string fileName, int size,
        long? rangeStart, long? rangeEnd, bool drip, bool acceptRanges)
    {
        byte[] pattern = PatternBytes(size);
        int bodyOffset = 0;
        int bodyLength = size;

        var head = new StringBuilder();
        if (acceptRanges && rangeStart.HasValue)
        {
            long start = Math.Max(0, rangeStart.Value);
            long end = rangeEnd.HasValue ? Math.Min(rangeEnd.Value, size - 1L) : size - 1L;

            if (start >= size || end < start)
            {
                await WriteTextResponse(stream, 416, "Requested Range Not Satisfiable",
                    $"Content-Range: bytes */{size}\r\n").ConfigureAwait(false);
                return;
            }

            bodyOffset = (int)start;
            bodyLength = (int)(end - start + 1);
            head.Append("HTTP/1.1 206 Partial Content\r\n");
            head.Append($"Content-Range: bytes {start}-{end}/{size}\r\n");
        }
        else
        {
            head.Append("HTTP/1.1 200 OK\r\n");
        }

        head.Append("Content-Type: application/octet-stream\r\n");
        head.Append($"Content-Length: {bodyLength}\r\n");
        if (acceptRanges)
            head.Append("Accept-Ranges: bytes\r\n");
        head.Append($"Content-Disposition: attachment; filename=\"{fileName}\"\r\n");
        head.Append("Connection: close\r\n");
        head.Append("\r\n");

        byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);

        if (!drip)
        {
            await stream.WriteAsync(pattern, bodyOffset, bodyLength).ConfigureAwait(false);
            return;
        }

        // Drip-feed the body so pause/resume/cancel have a real in-flight window to land in.
        int written = 0;
        while (written < bodyLength)
        {
            int block = Math.Min(DripBlockSize, bodyLength - written);
            await stream.WriteAsync(pattern, bodyOffset + written, block).ConfigureAwait(false);
            written += block;
            await Task.Delay(DripDelayMs).ConfigureAwait(false);
        }
    }

    private static Task WriteTextResponse(NetworkStream stream, int statusCode, string statusText,
        string extraHeaders = "")
    {
        string reason = statusCode switch {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            416 => "Requested Range Not Satisfiable",
            _ => "Error"
        };

        byte[] body = Encoding.ASCII.GetBytes(statusText);
        string head = $"HTTP/1.1 {statusCode} {reason}\r\n" +
                      "Content-Type: text/plain\r\n" +
                      $"Content-Length: {body.Length}\r\n" +
                      extraHeaders +
                      "Connection: close\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        return WriteAll(stream, headBytes, body);
    }

    private static async Task WriteAll(NetworkStream stream, byte[] head, byte[] body)
    {
        await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _listener.Stop(); // releases the pending AcceptTcpClientAsync
        }
        catch
        {
            // best effort shutdown
        }

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // best effort shutdown
        }

        _shutdown.Dispose();
    }
}
