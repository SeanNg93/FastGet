using System.Net;
using System.Net.Http.Headers;

namespace FastGet.Core;

public sealed record ProbeResult(Uri FinalUri, long Size, bool Resumable, string FileName, string? ContentType);

public sealed class HttpStatusException(HttpStatusCode code)
    : Exception($"Server returned {(int)code} {code}")
{
    public HttpStatusCode Code { get; } = code;

    /// <summary>4xx errors other than timeouts/throttling will not go away by retrying.</summary>
    public bool IsFatal => (int)Code is >= 400 and < 500 and not 408 and not 429;
}

public static class Http
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 20,
        MaxConnectionsPerServer = 64,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static HttpRequestMessage NewRequest(Uri uri, string? referer, string? cookies, string userAgent)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!string.IsNullOrEmpty(referer)) req.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrEmpty(cookies)) req.Headers.TryAddWithoutValidation("Cookie", cookies);
        return req;
    }

    /// <summary>
    /// Asks for the first byte. A 206 with Content-Range proves the server supports ranges (resume + multiple
    /// connections); a plain 200 means one sequential connection only.
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(HttpClient client, string url, string? referer, string? cookies,
        string userAgent, CancellationToken ct)
    {
        var uri = new Uri(url);
        using var req = NewRequest(uri, referer, cookies, userAgent);
        req.Headers.Range = new RangeHeaderValue(0, 0);
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Some servers reject a range on empty files; fall back to a plain request.
            using var plain = NewRequest(uri, referer, cookies, userAgent);
            using var plainResp = await client.SendAsync(plain, HttpCompletionOption.ResponseHeadersRead, ct);
            return Describe(plainResp, uri);
        }
        return Describe(resp, uri);
    }

    static ProbeResult Describe(HttpResponseMessage resp, Uri requested)
    {
        if (!resp.IsSuccessStatusCode) throw new HttpStatusException(resp.StatusCode);
        var final = resp.RequestMessage?.RequestUri ?? requested;
        long size = -1;
        bool resumable = false;
        if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange is { Length: { } len })
        {
            size = len;
            resumable = true;
        }
        else if (resp.Content.Headers.ContentLength is { } cl)
        {
            size = cl;
        }
        var type = resp.Content.Headers.ContentType?.MediaType;
        return new ProbeResult(final, size, resumable, FileNameFor(resp, final, type), type);
    }

    static string FileNameFor(HttpResponseMessage resp, Uri uri, string? contentType)
    {
        var cd = resp.Content.Headers.ContentDisposition;
        var name = cd?.FileNameStar ?? cd?.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(name)) name = FileNameFromUrl(uri.ToString());
        if (!Path.HasExtension(name) && contentType != null && MimeExtensions.TryGetValue(contentType, out var ext))
            name += ext;
        return Sanitize(name);
    }

    public static string FileNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "download";
        var last = uri.Segments.LastOrDefault()?.Trim('/') ?? "";
        var name = Uri.UnescapeDataString(last);
        return string.IsNullOrWhiteSpace(name) ? (uri.Host.Length > 0 ? uri.Host : "download") : Sanitize(name);
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return clean.Length == 0 ? "download" : clean.Length > 200 ? clean[..200] : clean;
    }

    static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text/html"] = ".html", ["text/plain"] = ".txt", ["application/pdf"] = ".pdf",
        ["application/zip"] = ".zip", ["application/json"] = ".json", ["video/mp4"] = ".mp4",
        ["audio/mpeg"] = ".mp3", ["image/jpeg"] = ".jpg", ["image/png"] = ".png",
        ["application/x-msdownload"] = ".exe", ["application/x-7z-compressed"] = ".7z",
        ["application/vnd.rar"] = ".rar", ["application/x-rar-compressed"] = ".rar",
    };
}

/// <summary>Token bucket shared by every connection so the limit applies to the total rate.</summary>
public sealed class SpeedLimiter
{
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    double _tokens;
    double _last;

    public long BytesPerSecond { get; set; }

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        var limit = BytesPerSecond;
        if (limit <= 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            var now = _clock.Elapsed.TotalSeconds;
            _tokens = Math.Min(limit, _tokens + (now - _last) * limit);
            _last = now;
            _tokens -= bytes;
            if (_tokens < 0)
                await Task.Delay(TimeSpan.FromSeconds(-_tokens / limit), ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
