using System.Buffers;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FastGet.Core;

public interface IDownloadTask
{
    event Action? Changed;
    Task RunAsync();
    void Cancel();
}

/// <summary>
/// Runs one download with several connections. Each connection owns a segment; when a connection finishes it
/// takes over half of the largest remaining segment ("dynamic segmentation"), so all connections stay busy
/// until the end instead of the last slow segment dragging on alone.
/// The number of connections adapts to the server: if it answers 429/503 to an extra connection, that connection's
/// work goes back to the one it was split from and the download continues with fewer connections.
/// </summary>
public sealed class DownloadTask : IDownloadTask
{
    public const long MinSplitBytes = 256 * 1024;
    const int BufferSize = 64 * 1024;
    /// <summary>A connection that receives nothing for this long is dropped and reopened. Settable for tests.</summary>
    public static TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(10);
    static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(8);

    readonly DownloadItem _item;
    readonly HttpClient _http;
    readonly AppSettings _settings;
    readonly SpeedLimiter _limiter;
    readonly HostLimits _hostLimits;
    readonly CancellationTokenSource _cts = new();
    SafeFileHandle? _file;
    Uri _uri;
    Exception? _fatal;
    int _allowed;
    int _workers;
    long _lastLimitTicks = DateTime.UtcNow.Ticks;
    /// <summary>After this long without a 429/503, try one more connection (up to the configured number).</summary>
    static readonly TimeSpan RampUpInterval = TimeSpan.FromSeconds(10);

    public DownloadTask(DownloadItem item, HttpClient http, AppSettings settings, SpeedLimiter limiter, HostLimits? hostLimits = null)
    {
        _item = item;
        _http = http;
        _settings = settings;
        _limiter = limiter;
        _hostLimits = hostLimits ?? new HostLimits();
        _uri = new Uri(item.Url);
    }

    public event Action? Changed;

    public void Cancel() => _cts.Cancel();

    /// <summary>Most connections running at the same moment during this download (for diagnostics and tests).</summary>
    public int PeakConnections { get; private set; }

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        _item.Status = DownloadStatus.Connecting;
        _item.LastTry = DateTime.Now;
        _item.Error = null;
        Changed?.Invoke();
        try
        {
            await PrepareAsync(ct);
            _item.Status = DownloadStatus.Downloading;
            Changed?.Invoke();

            int connections = _item.Resumable && _item.TotalSize > 0 ? _hostLimits.Get(_uri.Host, _settings.Connections) : 1;
            _allowed = connections;
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                await SuperviseAsync(connections, run);
            }
            catch when (_fatal != null && !ct.IsCancellationRequested)
            {
            }
            ct.ThrowIfCancellationRequested();
            if (_fatal != null) throw _fatal;

            lock (_item.Segments)
            {
                if (_item.Segments.Any(s => !s.IsDone))
                    throw new IOException("Download ended before all parts were received.");
            }
            if (_item.TotalSize < 0) _item.TotalSize = _item.Downloaded;
            SetSparse(_file!, false);
            _file?.Dispose();
            _file = null;
            Finish();
            _item.Status = DownloadStatus.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _item.Status = DownloadStatus.Paused;
        }
        catch (Exception ex)
        {
            _item.Status = DownloadStatus.Error;
            _item.Error = ex is HttpRequestException { InnerException: { } inner } ? inner.Message : ex.Message;
        }
        finally
        {
            _file?.Dispose();
            lock (_item.Segments)
            {
                foreach (var s in _item.Segments)
                {
                    s.Active = false;
                    s.Info = "";
                }
            }
            _item.Speed = 0;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Runs the workers and, while the server has not refused a connection for a while, adds one more connection
    /// at a time up to the configured number (additive increase after a 429/503 decrease).
    /// </summary>
    async Task SuperviseAsync(int connections, CancellationTokenSource run)
    {
        var tasks = new List<Task>();
        int next = 0;
        for (; next < connections; next++) tasks.Add(WorkerAsync(next, run));
        int target = _item.Resumable && _item.TotalSize > 0 ? _settings.Connections : 1;
        while (true)
        {
            var all = Task.WhenAll(tasks);
            await Task.WhenAny(all, Task.Delay(1000));
            if (all.IsCompleted)
            {
                await all; // surface worker exceptions
                return;
            }
            if (run.IsCancellationRequested) continue;
            var quiet = DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastLimitTicks), DateTimeKind.Utc);
            if (Volatile.Read(ref _allowed) < target && quiet >= RampUpInterval && HasSplittableWork())
            {
                Interlocked.Increment(ref _allowed);
                Interlocked.Exchange(ref _lastLimitTicks, DateTime.UtcNow.Ticks); // space out the next probe
                tasks.Add(WorkerAsync(next++, run));
            }
        }
    }

    bool HasSplittableWork()
    {
        lock (_item.Segments) return _item.Segments.Any(s => !s.IsDone && (!s.Active || s.Remaining >= 4 * MinSplitBytes));
    }

    async Task PrepareAsync(CancellationToken ct)
    {
        var ua = _item.UserAgent ?? _settings.UserAgent;
        ProbeResult probe;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                probe = await Http.ProbeAsync(_http, _item.Url, _item.Referer, _item.Cookies, ua, ct);
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt <= _settings.MaxRetries && IsRetryable(ex))
            {
                await Task.Delay(RetryDelay(attempt, ex), ct);
            }
        }
        _uri = probe.FinalUri;

        bool canContinue = _item.Segments.Count > 0
                           && probe.Resumable
                           && _item.Resumable
                           && probe.Size == _item.TotalSize
                           && File.Exists(_item.PartPath);
        if (_item.Segments.Count > 0 && !canContinue && _item.Downloaded > 0)
            _item.Error = probe.Resumable ? "File changed on server; restarting." : "Server does not support resume; restarting.";

        _item.TotalSize = probe.Size;
        _item.Resumable = probe.Resumable;
        if (string.IsNullOrEmpty(_item.FileName)) _item.FileName = probe.FileName;

        Directory.CreateDirectory(_item.SaveFolder);
        if (!canContinue)
        {
            lock (_item.Segments)
            {
                _item.Segments.Clear();
                _item.Segments.Add(new Segment { Start = 0, End = probe.Resumable && probe.Size > 0 ? probe.Size - 1 : -1 });
            }
            File.Delete(_item.PartPath);
        }
        _file = File.OpenHandle(_item.PartPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        // Connections write far apart in the file. In a normal file every write past the end makes Windows fill the
        // gap with zeros first (measured ~47 ms per 190 MB on an SSD, much more on HDDs); a sparse file skips that.
        SetSparse(_file, true);
    }

    static bool IsRetryable(Exception ex) =>
        ex is HttpStatusException { IsFatal: false } or HttpRequestException or IOException or TimeoutException
            or TaskCanceledException;

    /// <summary>0.5 s, 1 s, 2 s, 4 s, then 8 s; a server-provided Retry-After wins (capped at 60 s).</summary>
    static TimeSpan RetryDelay(int attempt, Exception? ex)
    {
        if (ex is HttpStatusException { RetryAfter: { } after }) return after < TimeSpan.FromSeconds(60) ? after : TimeSpan.FromSeconds(60);
        var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, Math.Min(attempt - 1, 10)));
        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    }

    async Task WorkerAsync(int index, CancellationTokenSource run)
    {
        var ct = run.Token;
        // Open connections gradually, like a browser would, instead of hitting the server all at once.
        if (index > 0) await Task.Delay(index * 150, ct);
        Interlocked.Increment(ref _workers);
        bool retired = false;
        try
        {
            retired = await WorkLoopAsync(index, run);
        }
        finally
        {
            if (!retired) Interlocked.Decrement(ref _workers);
        }
    }

    /// <summary>Returns true when the worker already removed itself from the worker count.</summary>
    async Task<bool> WorkLoopAsync(int index, CancellationTokenSource run)
    {
        var ct = run.Token;
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            // The server told us to use fewer connections: surplus workers stop instead of fighting for a slot.
            if (TryRetire()) return true;
            var seg = Acquire(out var splitFrom);
            if (seg == null) return false;
            Exception? error = null;
            bool received = false;
            try
            {
                await FetchAsync(seg, ct, () =>
                {
                    if (received) return;
                    received = true;
                    _hostLimits.Observe(_uri.Host, ActiveCount());
                });
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                error = ex;
                SetInfo(seg, "Error: " + (ex is HttpRequestException { InnerException: { } inner } ? inner.Message : ex.Message));
            }
            finally
            {
                Release(seg);
            }
            if (error == null) continue;

            // An extra connection refused with 429/503 while others are working means "too many connections":
            // give its fresh piece back to the connection it was split from and stop this one immediately.
            if (!received && error is HttpStatusException { IsServerLimit: true } && OtherActive(seg))
            {
                Unsplit(seg, splitFrom);
                int active = ActiveCount();
                Interlocked.Exchange(ref _lastLimitTicks, DateTime.UtcNow.Ticks);
                InterlockedMin(ref _allowed, Math.Max(1, active));
                _hostLimits.Lower(_uri.Host, Math.Max(1, active));
                return false;
            }

            bool fatal = error is HttpStatusException { IsFatal: true } or UnauthorizedAccessException;
            if (fatal || ++failures > _settings.MaxRetries)
            {
                // Extra connections that keep failing are not an error while others still make progress.
                if (!fatal && index > 0 && OtherActive(seg)) return false;
                _fatal ??= error;
                run.Cancel();
                return false;
            }
            // A connection that went silent is reopened at once; other errors back off briefly.
            if (error is not TimeoutException || failures > 1)
                await Task.Delay(RetryDelay(failures, error), ct);
        }
        return false;
    }

    bool TryRetire()
    {
        while (true)
        {
            int workers = Volatile.Read(ref _workers);
            if (workers <= Volatile.Read(ref _allowed)) return false;
            if (Interlocked.CompareExchange(ref _workers, workers - 1, workers) == workers) return true;
        }
    }

    static void InterlockedMin(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) > value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    Segment? Acquire(out Segment? splitFrom)
    {
        splitFrom = null;
        lock (_item.Segments)
        {
            var idle = _item.Segments.FirstOrDefault(s => !s.Active && !s.IsDone);
            if (idle != null)
            {
                idle.Active = true;
                idle.Info = "Connecting...";
                Track();
                return idle;
            }
            if (!_item.Resumable || _item.TotalSize <= 0) return null;

            var largest = _item.Segments.Where(s => s.Active && !s.IsDone).MaxBy(s => s.Remaining);
            if (largest == null || largest.Remaining < 2 * MinSplitBytes) return null;

            long mid = largest.Position + largest.Remaining / 2;
            var split = new Segment { Start = mid, End = largest.End, Active = true, Info = "Connecting..." };
            largest.End = mid - 1;
            _item.Segments.Insert(_item.Segments.IndexOf(largest) + 1, split);
            splitFrom = largest;
            Track();
            return split;
        }
    }

    void Track() => PeakConnections = Math.Max(PeakConnections, _item.Segments.Count(s => s.Active));

    /// <summary>
    /// Undo a split whose new connection was refused: the original connection's request still covers the range,
    /// so extending its end lets it keep streaming those bytes without a new request.
    /// </summary>
    void Unsplit(Segment seg, Segment? from)
    {
        lock (_item.Segments)
        {
            if (from == null || seg.Downloaded != 0 || !from.Active || from.End != seg.Start - 1) return;
            from.End = seg.End;
            _item.Segments.Remove(seg);
        }
    }

    void Release(Segment seg)
    {
        lock (_item.Segments) seg.Active = false;
    }

    bool OtherActive(Segment seg)
    {
        lock (_item.Segments) return _item.Segments.Any(s => s != seg && s.Active);
    }

    int ActiveCount()
    {
        lock (_item.Segments) return _item.Segments.Count(s => s.Active);
    }

    void SetInfo(Segment seg, string info)
    {
        lock (_item.Segments) seg.Info = info;
    }

    async Task FetchAsync(Segment seg, CancellationToken ct, Action onData)
    {
        long from, to;
        lock (_item.Segments)
        {
            // Without range support the only option after a failure is to start over.
            if (!_item.Resumable) seg.Downloaded = 0;
            from = seg.Position;
            to = seg.End;
        }

        using var req = Http.NewRequest(_uri, _item.Referer, _item.Cookies, _item.UserAgent ?? _settings.UserAgent);
        if (_item.Resumable) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to >= 0 ? to : null);
        using var resp = await Http.SendAsync(_http, req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw HttpStatusException.From(resp);
        if (_item.Resumable && resp.StatusCode != HttpStatusCode.PartialContent)
            throw new IOException("Server ignored the range request.");

        SetInfo(seg, "Receiving data...");
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                idle.CancelAfter(ReadTimeout);
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"No data for {ReadTimeout.TotalSeconds:0} seconds; reconnecting.");
                }

                if (read == 0)
                {
                    lock (_item.Segments)
                    {
                        if (seg.End < 0) seg.End = seg.Position - 1; // unknown size: end of stream is the end of file
                        else if (!seg.IsDone) throw new IOException("Connection closed early.");
                    }
                    return;
                }
                onData();

                await _limiter.WaitAsync(read, ct);
                long pos;
                int take;
                lock (_item.Segments)
                {
                    pos = seg.Position;
                    // Another connection may have taken over the tail of this segment meanwhile.
                    take = seg.End < 0 ? read : (int)Math.Min(read, seg.End - pos + 1);
                }
                if (take > 0)
                {
                    RandomAccess.Write(_file!, buffer.AsSpan(0, take), pos);
                    lock (_item.Segments) seg.Downloaded += take;
                }
                lock (_item.Segments)
                {
                    if (seg.IsDone) return;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    void Finish()
    {
        var part = _item.PartPath;
        if (File.Exists(_item.FullPath))
            _item.FileName = DownloadManager.UniqueName(_item.SaveFolder, _item.FileName, _ => false);
        File.Move(part, _item.FullPath);
    }

    /// <summary>Best effort: unsupported file systems (FAT32, network shares) simply keep the normal behaviour.</summary>
    static void SetSparse(SafeFileHandle file, bool sparse)
    {
        if (!OperatingSystem.IsWindows() || file.IsInvalid || file.IsClosed) return;
        const uint FSCTL_SET_SPARSE = 0x900C4;
        // FILE_SET_SPARSE_BUFFER is a single BOOLEAN byte.
        var flag = new byte[] { (byte)(sparse ? 1 : 0) };
        DeviceIoControl(file, FSCTL_SET_SPARSE, flag, flag.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize, IntPtr outBuffer, int outSize,
        out int returned, IntPtr overlapped);
}
