using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FastGet.Core;

public sealed record HlsVariant(Uri Uri, long Bandwidth, string? Resolution, string? Codecs, bool SeparateAudio);

public sealed record HlsKey(string Method, Uri? Uri, byte[]? Iv);

public sealed record HlsSegment(Uri Uri, long Sequence, double Duration, HlsKey? Key, long? RangeOffset, long? RangeLength);

public sealed class HlsMediaPlaylist
{
    public List<HlsSegment> Segments { get; } = new();
    public Uri? InitUri { get; set; }
    public long? InitOffset { get; set; }
    public long? InitLength { get; set; }
    public bool IsEndList { get; set; }
    /// <summary>fMP4 segments (EXT-X-MAP) join into .mp4; MPEG-TS segments into .ts.</summary>
    public string Extension => InitUri != null ? ".mp4" : ".ts";
    public double Duration => Segments.Sum(s => s.Duration);
}

/// <summary>A downloadable choice offered to the user for a captured video.</summary>
public sealed record StreamOption(string Url, DownloadKind Kind, string Label, long Bandwidth, long Size, string Extension);

public static partial class Hls
{
    [GeneratedRegex("""([A-Z0-9-]+)=("[^"]*"|[^,]*)""")]
    private static partial Regex AttributeRegex();

    public static bool LooksLikePlaylist(string url, string? contentType) =>
        (contentType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ?? false)
        || new Uri(url).AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    static Dictionary<string, string> Attributes(string line)
    {
        var colon = line.IndexOf(':');
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (colon < 0) return dict;
        foreach (Match m in AttributeRegex().Matches(line[(colon + 1)..]))
            dict[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
        return dict;
    }

    static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    public static List<HlsVariant> ParseMaster(string text, Uri baseUri)
    {
        var audioGroupsWithUri = new HashSet<string>();
        foreach (var line in Lines(text).Where(l => l.StartsWith("#EXT-X-MEDIA:")))
        {
            var a = Attributes(line);
            if (a.GetValueOrDefault("TYPE") == "AUDIO" && a.ContainsKey("URI") && a.TryGetValue("GROUP-ID", out var g))
                audioGroupsWithUri.Add(g);
        }

        var variants = new List<HlsVariant>();
        Dictionary<string, string>? pending = null;
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                pending = Attributes(line);
            }
            else if (!line.StartsWith('#') && pending != null)
            {
                long.TryParse(pending.GetValueOrDefault("BANDWIDTH"), out var bw);
                bool separateAudio = pending.TryGetValue("AUDIO", out var group) && audioGroupsWithUri.Contains(group);
                variants.Add(new HlsVariant(new Uri(baseUri, line), bw, pending.GetValueOrDefault("RESOLUTION"),
                    pending.GetValueOrDefault("CODECS"), separateAudio));
                pending = null;
            }
        }
        return variants.OrderByDescending(v => v.Bandwidth).ToList();
    }

    public static HlsMediaPlaylist ParseMedia(string text, Uri baseUri)
    {
        var playlist = new HlsMediaPlaylist();
        long sequence = 0;
        double duration = 0;
        HlsKey? key = null;
        long? rangeLength = null, rangeOffset = null;
        long nextOffset = 0;
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:"))
                sequence = long.Parse(line[22..], CultureInfo.InvariantCulture);
            else if (line.StartsWith("#EXTINF:"))
                double.TryParse(line[8..].Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            else if (line.StartsWith("#EXT-X-ENDLIST"))
                playlist.IsEndList = true;
            else if (line.StartsWith("#EXT-X-KEY:"))
                key = ParseKey(Attributes(line), baseUri);
            else if (line.StartsWith("#EXT-X-MAP:"))
            {
                var a = Attributes(line);
                playlist.InitUri = new Uri(baseUri, a["URI"]);
                if (a.TryGetValue("BYTERANGE", out var br))
                {
                    var (len, off) = ParseRange(br);
                    playlist.InitLength = len;
                    playlist.InitOffset = off ?? 0;
                }
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:"))
            {
                var (len, off) = ParseRange(line[17..]);
                rangeLength = len;
                rangeOffset = off ?? nextOffset;
            }
            else if (!line.StartsWith('#'))
            {
                playlist.Segments.Add(new HlsSegment(new Uri(baseUri, line), sequence, duration, key, rangeOffset, rangeLength));
                if (rangeLength is { } l) nextOffset = rangeOffset!.Value + l;
                sequence++;
                duration = 0;
                rangeLength = rangeOffset = null;
            }
        }
        return playlist;
    }

    static HlsKey? ParseKey(Dictionary<string, string> a, Uri baseUri)
    {
        var method = a.GetValueOrDefault("METHOD", "NONE");
        if (method == "NONE") return null;
        if (method != "AES-128")
            throw new NotSupportedException($"This stream is protected ({method}/DRM) and cannot be downloaded.");
        byte[]? iv = null;
        if (a.TryGetValue("IV", out var hex))
            iv = Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);
        return new HlsKey(method, new Uri(baseUri, a["URI"]), iv);
    }

    static (long Length, long? Offset) ParseRange(string value)
    {
        var parts = value.Split('@');
        return (long.Parse(parts[0], CultureInfo.InvariantCulture),
            parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : null);
    }

    /// <summary>Without an explicit IV, AES-128 uses the media sequence number as a 128-bit big-endian value.</summary>
    public static byte[] Decrypt(byte[] data, byte[] key, byte[]? iv, long sequence)
    {
        if (iv == null)
        {
            iv = new byte[16];
            BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), sequence);
        }
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
    }

    public static async Task<(string Text, Uri FinalUri)> FetchTextAsync(HttpClient http, Uri uri, string? referer, string? cookies,
        string userAgent, CancellationToken ct)
    {
        using var req = Http.NewRequest(uri, referer, cookies, userAgent);
        using var resp = await Http.SendAsync(http, req, HttpCompletionOption.ResponseContentRead, ct);
        if (!resp.IsSuccessStatusCode) throw HttpStatusException.From(resp);
        return (await resp.Content.ReadAsStringAsync(ct), resp.RequestMessage?.RequestUri ?? uri);
    }

    public static async Task<byte[]> FetchBytesAsync(HttpClient http, Uri uri, long? offset, long? length, string? referer,
        string? cookies, string userAgent, SpeedLimiter limiter, Action<int> progress, CancellationToken ct)
    {
        using var req = Http.NewRequest(uri, referer, cookies, userAgent);
        if (length is { } len) req.Headers.Range = new RangeHeaderValue(offset ?? 0, (offset ?? 0) + len - 1);
        using var resp = await Http.SendAsync(http, req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw HttpStatusException.From(resp);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            int read;
            try { read = await stream.ReadAsync(buffer, idle.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Connection timed out."); }
            if (read == 0) break;
            await limiter.WaitAsync(read, ct);
            ms.Write(buffer, 0, read);
            progress(read);
        }
        return ms.ToArray();
    }

    /// <summary>Turns a captured URL into choices: each quality of an HLS master playlist, or the single file.</summary>
    public static async Task<List<StreamOption>> InspectAsync(HttpClient http, string url, string? referer, string? cookies,
        string userAgent, CancellationToken ct)
    {
        var uri = new Uri(url);
        var probe = await Http.ProbeAsync(http, url, referer, cookies, userAgent, ct);
        if (!LooksLikePlaylist(url, probe.ContentType))
            return [new StreamOption(url, DownloadKind.File, $"Video file · {Path.GetExtension(probe.FileName).TrimStart('.').ToUpperInvariant()}",
                0, probe.Size, Path.GetExtension(probe.FileName))];

        var (text, final) = await FetchTextAsync(http, uri, referer, cookies, userAgent, ct);
        if (IsMaster(text))
        {
            return ParseMaster(text, final).Select(v => new StreamOption(v.Uri.ToString(), DownloadKind.Hls,
                string.Join(" · ", new[]
                {
                    v.Resolution ?? "Stream",
                    v.Bandwidth > 0 ? $"{v.Bandwidth / 1000.0 / 1000.0:0.0#} Mbps" : null,
                    v.Codecs,
                    v.SeparateAudio ? "video only (audio is a separate track)" : null,
                }.Where(x => !string.IsNullOrEmpty(x))), v.Bandwidth, -1, ".ts")).ToList();
        }
        var media = ParseMedia(text, final);
        var length = TimeSpan.FromSeconds(media.Duration);
        return [new StreamOption(final.ToString(), DownloadKind.Hls,
            $"HLS stream · {media.Segments.Count} segments · {(int)length.TotalMinutes}:{length.Seconds:00}" + (media.IsEndList ? "" : " · live"),
            0, -1, media.Extension)];
    }

    /// <summary>Path to ffmpeg if installed, used only to remux .ts into .mp4 without re-encoding.</summary>
    public static string? FindFfmpeg()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Select(dir => Path.Combine(dir.Trim(), "ffmpeg.exe")).FirstOrDefault(File.Exists);
    }
}

/// <summary>Downloads every segment of an HLS media playlist in parallel and joins them in order.</summary>
public sealed class HlsDownloadTask : IDownloadTask
{
    readonly DownloadItem _item;
    readonly HttpClient _http;
    readonly AppSettings _settings;
    readonly SpeedLimiter _limiter;
    readonly CancellationTokenSource _cts = new();
    readonly Dictionary<Uri, Task<byte[]>> _keys = new();

    public HlsDownloadTask(DownloadItem item, HttpClient http, AppSettings settings, SpeedLimiter limiter)
    {
        _item = item;
        _http = http;
        _settings = settings;
        _limiter = limiter;
    }

    public event Action? Changed;

    public void Cancel() => _cts.Cancel();

    string UserAgent => _item.UserAgent ?? _settings.UserAgent;

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        _item.Status = DownloadStatus.Connecting;
        _item.LastTry = DateTime.Now;
        _item.Error = null;
        Changed?.Invoke();
        try
        {
            var playlist = await LoadPlaylistAsync(ct);
            if (playlist.Segments.Count == 0) throw new IOException("The playlist has no segments.");
            if (playlist.Extension == ".mp4" && _item.FileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                _item.FileName = DownloadManager.UniqueName(_item.SaveFolder, Path.ChangeExtension(_item.FileName, ".mp4"), _ => false);

            var dir = _item.SegmentsPath;
            Directory.CreateDirectory(dir);
            var pending = new List<int>();
            long doneBytes = 0;
            for (int i = 0; i < playlist.Segments.Count; i++)
            {
                var f = SegmentFile(dir, i);
                if (File.Exists(f)) doneBytes += new FileInfo(f).Length; // finished in an earlier session
                else pending.Add(i);
            }
            _item.StreamTotal = playlist.Segments.Count;
            _item.StreamDone = playlist.Segments.Count - pending.Count;
            _item.StreamBytes = doneBytes;
            _item.Status = DownloadStatus.Downloading;
            Changed?.Invoke();

            if (playlist.InitUri != null && !File.Exists(Path.Combine(dir, "init.seg")))
            {
                var init = await WithRetries(() => Hls.FetchBytesAsync(_http, playlist.InitUri, playlist.InitOffset, playlist.InitLength,
                    _item.Referer, _item.Cookies, UserAgent, _limiter, _ => { }, ct), ct);
                await WriteAtomic(Path.Combine(dir, "init.seg"), init, ct);
            }

            int next = -1;
            async Task Worker()
            {
                while (true)
                {
                    int slot = Interlocked.Increment(ref next);
                    if (slot >= pending.Count) return;
                    int index = pending[slot];
                    await WithRetries(() => FetchSegmentAsync(playlist.Segments[index], SegmentFile(dir, index), ct), ct);
                    lock (_item.Segments) _item.StreamDone++;
                }
            }
            await Task.WhenAll(Enumerable.Range(0, Math.Min(_settings.Connections, Math.Max(1, pending.Count))).Select(_ => Worker()));

            await JoinAsync(dir, playlist.Segments.Count, ct);
            _item.TotalSize = new FileInfo(_item.FullPath).Length;
            _item.StreamBytes = _item.TotalSize;
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
            _item.Speed = 0;
            Changed?.Invoke();
        }
    }

    static string SegmentFile(string dir, int index) => Path.Combine(dir, $"{index:D6}.seg");

    async Task<HlsMediaPlaylist> LoadPlaylistAsync(CancellationToken ct)
    {
        var (text, final) = await Hls.FetchTextAsync(_http, new Uri(_item.Url), _item.Referer, _item.Cookies, UserAgent, ct);
        if (Hls.IsMaster(text))
        {
            // A master playlist was saved directly: take the best quality.
            var best = Hls.ParseMaster(text, final).FirstOrDefault() ?? throw new IOException("The master playlist has no streams.");
            (text, final) = await Hls.FetchTextAsync(_http, best.Uri, _item.Referer, _item.Cookies, UserAgent, ct);
        }
        return Hls.ParseMedia(text, final);
    }

    async Task FetchSegmentAsync(HlsSegment seg, string path, CancellationToken ct)
    {
        long counted = 0;
        try
        {
            var data = await Hls.FetchBytesAsync(_http, seg.Uri, seg.RangeOffset, seg.RangeLength, _item.Referer, _item.Cookies,
                UserAgent, _limiter, n =>
                {
                    counted += n;
                    lock (_item.Segments) _item.StreamBytes += n;
                }, ct);
            if (seg.Key is { Uri: { } keyUri })
            {
                var key = await KeyAsync(keyUri, ct);
                data = Hls.Decrypt(data, key, seg.Key.Iv, seg.Sequence);
            }
            await WriteAtomic(path, data, ct);
            lock (_item.Segments) _item.StreamBytes += data.Length - counted; // count stored (decrypted) size
        }
        catch
        {
            lock (_item.Segments) _item.StreamBytes -= counted;
            throw;
        }
    }

    Task<byte[]> KeyAsync(Uri uri, CancellationToken ct)
    {
        lock (_keys)
        {
            // A failed key fetch must not poison later retries.
            if (!_keys.TryGetValue(uri, out var task) || task.IsFaulted || task.IsCanceled)
                _keys[uri] = task = Hls.FetchBytesAsync(_http, uri, null, null, _item.Referer, _item.Cookies, UserAgent,
                    new SpeedLimiter(), _ => { }, ct);
            return task;
        }
    }

    static async Task WriteAtomic(string path, byte[] data, CancellationToken ct)
    {
        await File.WriteAllBytesAsync(path + ".tmp", data, ct);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    async Task<T> WithRetries<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await action(); }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < _settings.MaxRetries
                                       && ex is not HttpStatusException { IsFatal: true } and not NotSupportedException)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * (attempt + 1))), ct);
            }
        }
    }

    Task WithRetries(Func<Task> action, CancellationToken ct) => WithRetries(async () => { await action(); return 0; }, ct);

    async Task JoinAsync(string dir, int count, CancellationToken ct)
    {
        Directory.CreateDirectory(_item.SaveFolder);
        if (File.Exists(_item.FullPath))
            _item.FileName = DownloadManager.UniqueName(_item.SaveFolder, _item.FileName, _ => false);
        var part = _item.PartPath;
        await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var init = Path.Combine(dir, "init.seg");
            if (File.Exists(init)) await AppendAsync(output, init, ct);
            for (int i = 0; i < count; i++) await AppendAsync(output, SegmentFile(dir, i), ct);
        }
        File.Move(part, _item.FullPath);
        Directory.Delete(dir, recursive: true);
        await RemuxAsync(ct);
    }

    static async Task AppendAsync(Stream output, string file, CancellationToken ct)
    {
        await using var input = File.OpenRead(file);
        await input.CopyToAsync(output, ct);
    }

    /// <summary>MPEG-TS plays almost everywhere, but .mp4 is friendlier; remux only when ffmpeg is available.</summary>
    async Task RemuxAsync(CancellationToken ct)
    {
        if (!_item.FileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || Hls.FindFfmpeg() is not { } ffmpeg) return;
        var mp4Name = DownloadManager.UniqueName(_item.SaveFolder, Path.ChangeExtension(_item.FileName, ".mp4"), _ => false);
        var mp4 = Path.Combine(_item.SaveFolder, mp4Name);
        var psi = new ProcessStartInfo(ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", _item.FullPath, "-c", "copy", "-bsf:a", "aac_adtstoasc", mp4 })
            psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode == 0 && File.Exists(mp4))
        {
            File.Delete(_item.FullPath);
            _item.FileName = mp4Name;
        }
        else
        {
            File.Delete(mp4); // keep the .ts
        }
    }
}
