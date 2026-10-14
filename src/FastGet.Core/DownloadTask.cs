using System.Buffers;
using System.Net;
using Microsoft.Win32.SafeHandles;

namespace FastGet.Core;

/// <summary>
/// Runs one download with several connections. Each connection owns a segment; when a connection finishes it
/// takes over half of the largest remaining segment ("dynamic segmentation"), so all connections stay busy
/// until the end instead of the last slow segment dragging on alone.
/// </summary>
public sealed class DownloadTask
{
    public const long MinSplitBytes = 256 * 1024;
    const int BufferSize = 64 * 1024;
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    readonly DownloadItem _item;
    readonly HttpClient _http;
    readonly AppSettings _settings;
    readonly SpeedLimiter _limiter;
    readonly CancellationTokenSource _cts = new();
    SafeFileHandle? _file;
    Uri _uri;
    Exception? _fatal;

    public DownloadTask(DownloadItem item, HttpClient http, AppSettings settings, SpeedLimiter limiter)
    {
        _item = item;
        _http = http;
        _settings = settings;
        _limiter = limiter;
        _uri = new Uri(item.Url);
    }

    public event Action? Changed;

    public void Cancel() => _cts.Cancel();

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

            int connections = _item.Resumable && _item.TotalSize > 0 ? _settings.Connections : 1;
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var workers = Enumerable.Range(0, connections).Select(i => WorkerAsync(i, run)).ToArray();
            try
            {
                await Task.WhenAll(workers);
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
            _file?.Dispose();
            _file = null;
            Finish();
            _item.Status = DownloadStatus.Completed;
            _item.InQueue = false;
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

    async Task PrepareAsync(CancellationToken ct)
    {
        var ua = _item.UserAgent ?? _settings.UserAgent;
        var probe = await Http.ProbeAsync(_http, _item.Url, _item.Referer, _item.Cookies, ua, ct);
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
    }

    async Task WorkerAsync(int index, CancellationTokenSource run)
    {
        var ct = run.Token;
        // Open connections gradually, like a browser would, instead of hitting the server all at once.
        if (index > 0) await Task.Delay(index * 150, ct);
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var seg = Acquire();
            if (seg == null) return;
            Exception? error = null;
            try
            {
                await FetchAsync(seg, ct);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                error = ex;
                SetInfo(seg, "Error: " + ex.Message);
            }
            finally
            {
                Release(seg);
            }
            if (error == null) continue;

            bool fatal = error is HttpStatusException { IsFatal: true } or UnauthorizedAccessException;
            if (fatal || ++failures > _settings.MaxRetries)
            {
                // Extra connections that the server refuses are not an error while others still make progress.
                if (!fatal && index > 0 && OtherActive(seg)) return;
                _fatal ??= error;
                run.Cancel();
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * failures)), ct);
        }
    }

    Segment? Acquire()
    {
        lock (_item.Segments)
        {
            var idle = _item.Segments.FirstOrDefault(s => !s.Active && !s.IsDone);
            if (idle != null)
            {
                idle.Active = true;
                idle.Info = "Connecting...";
                return idle;
            }
            if (!_item.Resumable || _item.TotalSize <= 0) return null;

            var largest = _item.Segments.Where(s => s.Active && !s.IsDone).MaxBy(s => s.Remaining);
            if (largest == null || largest.Remaining < 2 * MinSplitBytes) return null;

            long mid = largest.Position + largest.Remaining / 2;
            var split = new Segment { Start = mid, End = largest.End, Active = true, Info = "Connecting..." };
            largest.End = mid - 1;
            _item.Segments.Insert(_item.Segments.IndexOf(largest) + 1, split);
            return split;
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

    void SetInfo(Segment seg, string info)
    {
        lock (_item.Segments) seg.Info = info;
    }

    async Task FetchAsync(Segment seg, CancellationToken ct)
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
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpStatusException(resp.StatusCode);
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
                    throw new TimeoutException("Connection timed out.");
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
}
