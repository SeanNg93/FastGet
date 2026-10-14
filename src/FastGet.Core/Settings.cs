using System.Text.Json;

namespace FastGet.Core;

public sealed class Category
{
    public const string GeneralName = "General";

    public string Name { get; set; } = "";
    /// <summary>Space-separated extensions without dots.</summary>
    public string Extensions { get; set; } = "";
    public string Folder { get; set; } = "";

    public bool Matches(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.');
        return ext.Length > 0 && Extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    public static List<Category> Defaults(string root) =>
    [
        new() { Name = GeneralName, Folder = root },
        new() { Name = "Compressed", Folder = Path.Combine(root, "Compressed"), Extensions = "zip rar 7z gz gzip tar tgz bz2 xz z arj lzh ace sit sitx sea r00 r01" },
        new() { Name = "Documents", Folder = Path.Combine(root, "Documents"), Extensions = "pdf doc docx xls xlsx ppt pptx pps txt rtf odt ods odp csv epub" },
        new() { Name = "Music", Folder = Path.Combine(root, "Music"), Extensions = "mp3 wav wma aac flac ogg m4a mpa aif ra opus" },
        new() { Name = "Programs", Folder = Path.Combine(root, "Programs"), Extensions = "exe msi msu msix appx apk dmg iso img bin jar" },
        new() { Name = "Video", Folder = Path.Combine(root, "Video"), Extensions = "mp4 mkv avi mov wmv flv webm mpg mpeg mpe m4v 3gp ogv qt rm rmvb asf ts" },
    ];
}

public enum WhenDoneAction { None, ExitApp, Shutdown, Sleep, Hibernate }

/// <summary>Scheduler for the main queue: when to start/stop it and what to do when it finishes.</summary>
public sealed class ScheduleSettings
{
    public bool StartEnabled { get; set; }
    /// <summary>"HH:mm", local time.</summary>
    public string StartTime { get; set; } = "02:00";
    /// <summary>true = every selected weekday; false = once on <see cref="OnceDate"/>.</summary>
    public bool Daily { get; set; } = true;
    public List<DayOfWeek> Days { get; set; } = Enum.GetValues<DayOfWeek>().ToList();
    /// <summary>"yyyy-MM-dd" for a one-time start.</summary>
    public string OnceDate { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
    public bool StopEnabled { get; set; }
    public string StopTime { get; set; } = "07:00";
    /// <summary>Retries for each queued file that fails.</summary>
    public int QueueRetries { get; set; } = 3;
    public WhenDoneAction WhenDone { get; set; }
    /// <summary>Close apps that block shutdown (shutdown /f).</summary>
    public bool ForceClose { get; set; }

    public bool DayAllowed(DateTime day) =>
        Daily ? Days.Contains(day.DayOfWeek) : day.ToString("yyyy-MM-dd") == OnceDate;

    /// <summary>Next start time for display, or null when the start schedule is off.</summary>
    public DateTime? NextStart(DateTime now)
    {
        if (!StartEnabled || !TryParseTime(StartTime, out var t)) return null;
        for (int i = 0; i < 8; i++)
        {
            var at = now.Date.AddDays(i) + t;
            if (at >= now && DayAllowed(at.Date)) return at;
        }
        return null;
    }

    public static bool TryParseTime(string value, out TimeSpan time) =>
        TimeSpan.TryParseExact(value, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out time);
}

public sealed class AppSettings
{
    public int Connections { get; set; } = 8;
    public int MaxConcurrent { get; set; } = 3;
    public int MaxRetries { get; set; } = 10;
    /// <summary>Global limit across all downloads; 0 = unlimited.</summary>
    public int SpeedLimitKBps { get; set; }
    public bool SpeedLimitEnabled { get; set; }
    public bool MonitorClipboard { get; set; } = true;
    public bool ShowStartDialog { get; set; } = true;
    public bool ShowProgressDialog { get; set; } = true;
    public bool ShowCompleteDialog { get; set; } = true;
    public string DownloadRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FastGet");
    /// <summary>Extensions that the clipboard monitor and browser extension take over. '*' wildcards allowed.</summary>
    public string AutoExtensions { get; set; } =
        "3gp 7z aac apk avi bin bz2 deb dmg epub exe flac flv gz gzip img iso jar m4a m4v mkv mov mp3 mp4 mpeg mpg msi msu ogg ogv pdf ppt pptx rar rmvb rpm tar tgz torrent wav webm wma wmv xz zip r0* r1*";
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    public List<Category> Categories { get; set; } = new();
    public ScheduleSettings Schedule { get; set; } = new();

    public long EffectiveSpeedLimit => SpeedLimitEnabled && SpeedLimitKBps > 0 ? SpeedLimitKBps * 1024L : 0;

    public Category ResolveCategory(string fileName) =>
        Categories.FirstOrDefault(c => c.Name != Category.GeneralName && c.Matches(fileName))
        ?? Categories.FirstOrDefault(c => c.Name == Category.GeneralName)
        ?? new Category { Name = Category.GeneralName, Folder = DownloadRoot };

    public bool IsAutoExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.');
        if (ext.Length == 0) return false;
        foreach (var pattern in AutoExtensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pattern.EndsWith('*')
                ? ext.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                : ext.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    public void Normalize()
    {
        Connections = Math.Clamp(Connections, 1, 32);
        Schedule ??= new ScheduleSettings();
        Schedule.QueueRetries = Math.Clamp(Schedule.QueueRetries, 0, 100);
        MaxConcurrent = Math.Clamp(MaxConcurrent, 1, 20);
        MaxRetries = Math.Clamp(MaxRetries, 0, 100);
        if (Categories.Count == 0) Categories = Category.Defaults(DownloadRoot);
        if (Categories.All(c => c.Name != Category.GeneralName))
            Categories.Insert(0, new Category { Name = Category.GeneralName, Folder = DownloadRoot });
    }
}

public static class JsonFile
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null;
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".corrupt", overwrite: true);
            return null;
        }
    }

    /// <summary>Atomic replace so a crash mid-write never loses the download list.</summary>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
