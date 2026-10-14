using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace FastGet.Core.Tests;

/// <summary>
/// Minimal HTTP/1.1 file server over raw sockets, so tests need no URL ACLs or admin rights.
/// It can also misbehave like real servers: limit parallel connections, never answer, or stall mid-body.
/// </summary>
sealed class TestServer : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    int _requests;
    int _active;
    int _peak;

    public TestServer(byte[] content, bool ranges = true)
    {
        Content = content;
        Ranges = ranges;
        _listener.Start();
        _ = AcceptLoop();
    }

    /// <summary>Serves several files by path instead of one Content for every URL.</summary>
    public TestServer(Dictionary<string, byte[]> routes) : this(Array.Empty<byte>())
    {
        Routes = routes;
    }

    public Dictionary<string, byte[]>? Routes { get; }
    public byte[] Content { get; }
    public bool Ranges { get; }
    public string? ContentDisposition { get; init; }
    /// <summary>Delay per 16 KB chunk, to keep a download running long enough to pause it.</summary>
    public int ChunkDelayMs { get; set; }
    /// <summary>Answer 429 when more than this many requests are being served at once (0 = unlimited).</summary>
    public int MaxConcurrent { get; init; }
    /// <summary>Request numbers (1-based) that get a connection but never a response.</summary>
    public HashSet<int> NeverAnswer { get; init; } = [];
    /// <summary>Request number → body bytes after which the connection goes silent but stays open.</summary>
    public Dictionary<int, long> StallAfter { get; init; } = [];
    /// <summary>Request number → status code to answer instead of the file.</summary>
    public Dictionary<int, int> StatusFor { get; init; } = [];

    public int Requests => _requests;
    public int PeakConcurrent => _peak;
    public string Url(string name = "file.bin") => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{name}";

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        using var _ = client;
        bool counted = false;
        try
        {
            var stream = client.GetStream();
            var header = await ReadHeader(stream);
            int number = Interlocked.Increment(ref _requests);

            if (NeverAnswer.Contains(number))
            {
                await Task.Delay(Timeout.Infinite, _cts.Token);
                return;
            }
            if (StatusFor.TryGetValue(number, out var forced))
            {
                await WriteStatus(stream, forced);
                return;
            }
            int active = Interlocked.Increment(ref _active);
            counted = true;
            if (MaxConcurrent > 0 && active > MaxConcurrent)
            {
                await WriteStatus(stream, 429);
                return;
            }
            InterlockedMax(ref _peak, active);

            var content = Content;
            if (Routes != null)
            {
                var path = Regex.Match(header, @"^GET (\S+)").Groups[1].Value.Split('?')[0];
                if (!Routes.TryGetValue(path, out content!))
                {
                    await WriteStatus(stream, 404);
                    return;
                }
            }

            long start = 0, end = content.Length - 1;
            var status = "200 OK";
            var extra = new StringBuilder();
            var m = Regex.Match(header, @"^Range:\s*bytes=(\d+)-(\d*)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (Ranges)
            {
                extra.Append("Accept-Ranges: bytes\r\n");
                if (m.Success)
                {
                    start = long.Parse(m.Groups[1].Value);
                    if (m.Groups[2].Value.Length > 0) end = Math.Min(end, long.Parse(m.Groups[2].Value));
                    status = "206 Partial Content";
                    extra.Append($"Content-Range: bytes {start}-{end}/{content.Length}\r\n");
                }
            }
            if (ContentDisposition != null) extra.Append($"Content-Disposition: {ContentDisposition}\r\n");
            long length = end - start + 1;
            var head = $"HTTP/1.1 {status}\r\nContent-Length: {length}\r\nContent-Type: application/octet-stream\r\n{extra}Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            long sent = 0;
            long stallAt = StallAfter.TryGetValue(number, out var s) ? s : long.MaxValue;
            for (long pos = start; pos <= end; pos += 16384)
            {
                if (sent >= stallAt)
                {
                    await Task.Delay(Timeout.Infinite, _cts.Token);
                    return;
                }
                int n = (int)Math.Min(16384, end - pos + 1);
                await stream.WriteAsync(content.AsMemory((int)pos, n));
                sent += n;
                if (ChunkDelayMs > 0) await Task.Delay(ChunkDelayMs);
            }
        }
        catch (IOException) { } // client hung up, e.g. after its segment was split
        catch (SocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            if (counted) Interlocked.Decrement(ref _active);
        }
    }

    static Task WriteStatus(NetworkStream stream, int code) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Status\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")).AsTask();

    static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    static async Task<string> ReadHeader(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n"))
        {
            if (await stream.ReadAsync(buf) == 0) break;
            sb.Append((char)buf[0]);
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
