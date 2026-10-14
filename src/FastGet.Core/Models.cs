using System.Text.Json.Serialization;

namespace FastGet.Core;

public enum DownloadStatus { Queued, Connecting, Downloading, Paused, Completed, Error }

/// <summary>File = one URL fetched by byte ranges; Hls = an HLS media playlist whose segments are fetched and joined.</summary>
public enum DownloadKind { File, Hls }

/// <summary>A byte range of the file fetched by one connection. End is inclusive; -1 means "until the stream ends" (size unknown).</summary>
public sealed class Segment
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Downloaded { get; set; }

    [JsonIgnore] public long Position => Start + Downloaded;
    [JsonIgnore] public long Remaining => End < 0 ? long.MaxValue : End - Position + 1;
    [JsonIgnore] public bool IsDone => End >= 0 && Position > End;
    [JsonIgnore] public bool Active { get; set; }
    [JsonIgnore] public string Info { get; set; } = "";

    public Segment Clone() => new() { Start = Start, End = End, Downloaded = Downloaded, Active = Active, Info = Info };
}

public sealed class DownloadItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DownloadKind Kind { get; set; }
    public string Url { get; set; } = "";
    public string? Referer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public string FileName { get; set; } = "";
    public string SaveFolder { get; set; } = "";
    public string Category { get; set; } = Core.Category.GeneralName;
    public string Description { get; set; } = "";
    public long TotalSize { get; set; } = -1;
    public bool Resumable { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Paused;
    public bool InQueue { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
    public DateTime? LastTry { get; set; }
    public string? Error { get; set; }
    /// <summary>Automatic retries the queue has made for this item since the queue was started.</summary>
    public int QueueAttempts { get; set; }

    // HLS progress: segments are the unit of work and the final size is unknown until the end.
    public int StreamTotal { get; set; }
    public int StreamDone { get; set; }
    public long StreamBytes { get; set; }

    /// <summary>Guarded by locking the list itself; workers split and advance segments concurrently.</summary>
    public List<Segment> Segments { get; set; } = new();

    [JsonIgnore] public string FullPath => Path.Combine(SaveFolder, FileName);
    [JsonIgnore] public string PartPath => FullPath + ".fgpart";
    /// <summary>Folder holding finished HLS segments, so a stream download can resume segment by segment.</summary>
    [JsonIgnore] public string SegmentsPath => FullPath + ".fgparts";
    [JsonIgnore] public double Speed { get; set; }
    [JsonIgnore] public bool IsActive => Status is DownloadStatus.Connecting or DownloadStatus.Downloading;

    [JsonIgnore]
    public long Downloaded
    {
        get
        {
            if (Kind == DownloadKind.Hls) return StreamBytes;
            lock (Segments) return Segments.Sum(s => s.Downloaded);
        }
    }

    /// <summary>Final size if known; for streams, extrapolated from the segments finished so far.</summary>
    [JsonIgnore]
    public long ExpectedSize => Kind == DownloadKind.Hls && Status != DownloadStatus.Completed
        ? StreamDone > 0 && StreamTotal > 0 ? StreamBytes * StreamTotal / StreamDone : -1
        : TotalSize;

    [JsonIgnore]
    public bool HasKnownProgress => Kind == DownloadKind.Hls ? StreamTotal > 0 : TotalSize > 0;

    [JsonIgnore]
    public double Progress => Status == DownloadStatus.Completed ? 1
        : Kind == DownloadKind.Hls ? StreamTotal > 0 ? (double)StreamDone / StreamTotal : 0
        : TotalSize > 0 ? Math.Min(1.0, (double)Downloaded / TotalSize) : 0;

    [JsonIgnore]
    public TimeSpan? TimeLeft => IsActive && Speed > 1 && ExpectedSize > 0
        ? TimeSpan.FromSeconds(Math.Max(0, ExpectedSize - Downloaded) / Speed)
        : null;

    public List<Segment> SnapshotSegments()
    {
        lock (Segments) return Segments.Select(s => s.Clone()).ToList();
    }

    /// <summary>Copy that is safe to serialize while workers are still running.</summary>
    public DownloadItem Snapshot()
    {
        var copy = (DownloadItem)MemberwiseClone();
        copy.Segments = SnapshotSegments();
        return copy;
    }
}

/// <summary>Request to add a download, from a dialog, the clipboard or the browser extension.</summary>
public sealed class NewDownload
{
    public DownloadKind Kind { get; set; }
    public string Url { get; set; } = "";
    public string? FileName { get; set; }
    public string? Folder { get; set; }
    public string? Category { get; set; }
    public string Description { get; set; } = "";
    public string? Referer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public long Size { get; set; } = -1;
    public bool Resumable { get; set; }
}

public enum AddMode { StartNow, Later, Paused }
