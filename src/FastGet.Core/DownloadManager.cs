namespace FastGet.Core;

/// <summary>Owns the download list, the main queue, persistence and speed accounting. Events fire on worker threads.</summary>
public sealed class DownloadManager : IDisposable
{
    readonly object _lock = new();
    readonly List<DownloadItem> _items;
    readonly Dictionary<Guid, IDownloadTask> _running = new();
    readonly Dictionary<Guid, (long Bytes, DateTime At)> _samples = new();
    readonly HttpClient _http = Http.CreateClient();
    readonly Timer _timer;
    readonly string _listPath;
    readonly string _settingsPath;
    bool _dirty;
    bool _queueStartedWork;
    DateTime _lastSave = DateTime.MinValue;

    public DownloadManager(string dataDir)
    {
        DataDir = dataDir;
        _listPath = Path.Combine(dataDir, "downloads.json");
        _settingsPath = Path.Combine(dataDir, "settings.json");
        Settings = JsonFile.Load<AppSettings>(_settingsPath) ?? new AppSettings();
        Settings.Normalize();
        Limiter.BytesPerSecond = Settings.EffectiveSpeedLimit;

        _items = JsonFile.Load<List<DownloadItem>>(_listPath) ?? new List<DownloadItem>();
        foreach (var item in _items.Where(i => i.IsActive))
            item.Status = DownloadStatus.Paused; // app was closed or crashed mid-download

        _timer = new Timer(_ => Tick(), null, 500, 500);
    }

    public string DataDir { get; }
    public AppSettings Settings { get; private set; }
    public SpeedLimiter Limiter { get; } = new();
    public bool QueueRunning { get; private set; }

    public event Action<DownloadItem>? ItemAdded;
    public event Action<DownloadItem>? ItemRemoved;
    public event Action<DownloadItem>? ItemChanged;
    public event Action<DownloadItem>? ItemCompleted;
    public event Action? QueueStateChanged;
    /// <summary>The queue ran out of work on its own (not stopped by the user) — the moment to exit or shut down.</summary>
    public event Action? QueueCompleted;

    public IReadOnlyList<DownloadItem> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public DownloadItem? Find(Guid id)
    {
        lock (_lock) return _items.FirstOrDefault(i => i.Id == id);
    }

    public Task<List<StreamOption>> InspectStreamAsync(string url, string? referer, string? cookies, string? userAgent, CancellationToken ct) =>
        Hls.InspectAsync(_http, url, referer, cookies, userAgent ?? Settings.UserAgent, ct);

    public Task<ProbeResult> ProbeAsync(string url, string? referer, string? cookies, string? userAgent, CancellationToken ct) =>
        Http.ProbeAsync(_http, url, referer, cookies, userAgent ?? Settings.UserAgent, ct);

    public DownloadItem Add(NewDownload d, AddMode mode)
    {
        var fileName = Http.Sanitize(string.IsNullOrWhiteSpace(d.FileName) ? Http.FileNameFromUrl(d.Url) : d.FileName);
        var category = Settings.Categories.FirstOrDefault(c => c.Name == d.Category) ?? Settings.ResolveCategory(fileName);
        var folder = string.IsNullOrWhiteSpace(d.Folder) ? category.Folder : d.Folder;

        DownloadItem item;
        lock (_lock)
        {
            item = new DownloadItem
            {
                Kind = d.Kind,
                Url = d.Url,
                Referer = d.Referer,
                Cookies = d.Cookies,
                UserAgent = d.UserAgent,
                SaveFolder = folder,
                FileName = UniqueName(folder, fileName, IsTaken),
                Category = category.Name,
                Description = d.Description,
                TotalSize = d.Size,
                Resumable = d.Resumable,
                Status = mode == AddMode.Later ? DownloadStatus.Queued : DownloadStatus.Paused,
                InQueue = mode == AddMode.Later,
            };
            _items.Add(item);
            _dirty = true;
        }
        ItemAdded?.Invoke(item);
        if (mode == AddMode.StartNow) Start(item);
        return item;
    }

    bool IsTaken(string path)
    {
        lock (_lock)
            return _items.Any(i => i.Status != DownloadStatus.Completed
                                   && string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"file.zip" -> "file (2).zip" while the name is used on disk, as a partial file, or by another download.</summary>
    public static string UniqueName(string folder, string fileName, Func<string, bool> taken)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = fileName;
        for (int n = 2; ; n++)
        {
            var path = Path.Combine(folder, candidate);
            if (!File.Exists(path) && !File.Exists(path + ".fgpart") && !Directory.Exists(path + ".fgparts") && !taken(path))
                return candidate;
            candidate = $"{stem} ({n}){ext}";
        }
    }

    public void Start(DownloadItem item)
    {
        IDownloadTask task;
        lock (_lock)
        {
            if (_running.ContainsKey(item.Id) || item.Status == DownloadStatus.Completed) return;
            task = item.Kind == DownloadKind.Hls
                ? new HlsDownloadTask(item, _http, Settings, Limiter)
                : new DownloadTask(item, _http, Settings, Limiter);
            _running[item.Id] = task;
            _samples[item.Id] = (item.Downloaded, DateTime.UtcNow);
        }
        task.Changed += () => Changed(item);
        _ = Task.Run(async () =>
        {
            await task.RunAsync();
            lock (_lock)
            {
                _running.Remove(item.Id);
                _samples.Remove(item.Id);
                _dirty = true;
            }
            Changed(item);
            if (item.Status == DownloadStatus.Completed) ItemCompleted?.Invoke(item);
            PumpQueue();
        });
    }

    public void Pause(DownloadItem item)
    {
        IDownloadTask? task;
        lock (_lock)
        {
            _running.TryGetValue(item.Id, out task);
            if (task == null && item.Status == DownloadStatus.Queued)
            {
                item.Status = DownloadStatus.Paused;
                _dirty = true;
            }
        }
        task?.Cancel();
        Changed(item);
    }

    public void PauseAll()
    {
        foreach (var item in Items) Pause(item);
        SetQueueRunning(false);
    }

    public void Remove(DownloadItem item, bool deleteFiles)
    {
        Pause(item);
        lock (_lock)
        {
            _items.Remove(item);
            _dirty = true;
        }
        if (deleteFiles || item.Status != DownloadStatus.Completed)
        {
            // Give a cancelled task a moment to close its file handle before deleting the partial file.
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        File.Delete(item.PartPath);
                        if (Directory.Exists(item.SegmentsPath)) Directory.Delete(item.SegmentsPath, recursive: true);
                        if (deleteFiles && item.Status == DownloadStatus.Completed) File.Delete(item.FullPath);
                        return;
                    }
                    catch (IOException) { await Task.Delay(250); }
                    catch (UnauthorizedAccessException) { return; }
                }
            });
        }
        ItemRemoved?.Invoke(item);
    }

    public void RemoveCompleted()
    {
        foreach (var item in Items.Where(i => i.Status == DownloadStatus.Completed)) Remove(item, deleteFiles: false);
    }

    /// <summary>Discard progress and fetch again, e.g. after the link expired and was refreshed.</summary>
    public void Redownload(DownloadItem item)
    {
        Pause(item);
        _ = Task.Run(async () =>
        {
            // The cancelled task must release the partial file before its progress can be thrown away.
            for (int i = 0; i < 100 && IsRunning(item); i++) await Task.Delay(50);
            lock (item.Segments) item.Segments.Clear();
            item.Status = DownloadStatus.Paused;
            item.TotalSize = -1;
            item.StreamDone = item.StreamTotal = 0;
            item.StreamBytes = 0;
            try { if (Directory.Exists(item.SegmentsPath)) Directory.Delete(item.SegmentsPath, recursive: true); } catch (IOException) { }
            if (File.Exists(item.FullPath) && !File.Exists(item.PartPath))
                item.FileName = UniqueName(item.SaveFolder, item.FileName, IsTaken);
            try { File.Delete(item.PartPath); } catch (IOException) { }
            Start(item);
        });
    }

    public bool IsRunning(DownloadItem item)
    {
        lock (_lock) return _running.ContainsKey(item.Id);
    }

    public void AddToQueue(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Completed) return;
        item.InQueue = true;
        if (!item.IsActive) item.Status = DownloadStatus.Queued;
        _dirty = true;
        Changed(item);
        PumpQueue();
    }

    public void StartQueue()
    {
        foreach (var item in Items.Where(i => i.InQueue && !i.IsActive && i.Status != DownloadStatus.Completed))
        {
            item.Status = DownloadStatus.Queued;
            item.QueueAttempts = 0;
            Changed(item);
        }
        _queueStartedWork = false;
        SetQueueRunning(true);
        PumpQueue();
    }

    public void StopQueue()
    {
        SetQueueRunning(false);
        foreach (var item in Items.Where(i => i.InQueue && i.IsActive)) Pause(item);
    }

    void SetQueueRunning(bool running)
    {
        lock (_lock)
        {
            if (QueueRunning == running) return;
            QueueRunning = running;
        }
        QueueStateChanged?.Invoke();
    }

    void PumpQueue()
    {
        List<DownloadItem> toStart;
        bool completed = false;
        lock (_lock)
        {
            if (!QueueRunning) return;
            // Failed queue items get another chance, up to the scheduler's retry count.
            foreach (var failed in _items.Where(i => i.InQueue && i.Status == DownloadStatus.Error && !_running.ContainsKey(i.Id)
                                                     && i.QueueAttempts < Settings.Schedule.QueueRetries))
            {
                failed.QueueAttempts++;
                failed.Status = DownloadStatus.Queued;
            }
            int free = Settings.MaxConcurrent - _running.Count;
            toStart = _items.Where(i => i.InQueue && i.Status == DownloadStatus.Queued && !_running.ContainsKey(i.Id))
                .Take(Math.Max(0, free)).ToList();
            if (toStart.Count > 0) _queueStartedWork = true;
            bool finished = toStart.Count == 0
                            && !_items.Any(i => i.InQueue && (i.Status == DownloadStatus.Queued || _running.ContainsKey(i.Id)));
            if (finished)
            {
                // Decided under the lock so two downloads finishing together cannot both report completion.
                QueueRunning = false;
                // An empty queue that "finishes" instantly must never trigger shutdown.
                completed = _queueStartedWork;
                _queueStartedWork = false;
            }
        }
        foreach (var item in toStart) Start(item);
        if (!QueueRunning && toStart.Count == 0)
        {
            QueueStateChanged?.Invoke();
            if (completed) QueueCompleted?.Invoke();
        }
    }

    public void UpdateSettings(AppSettings settings)
    {
        settings.Normalize();
        // Running tasks keep a reference to the old object; copy values into it so they pick up changes.
        Settings.Connections = settings.Connections;
        Settings.MaxConcurrent = settings.MaxConcurrent;
        Settings.MaxRetries = settings.MaxRetries;
        Settings.SpeedLimitKBps = settings.SpeedLimitKBps;
        Settings.SpeedLimitEnabled = settings.SpeedLimitEnabled;
        Settings.MonitorClipboard = settings.MonitorClipboard;
        Settings.ShowStartDialog = settings.ShowStartDialog;
        Settings.ShowProgressDialog = settings.ShowProgressDialog;
        Settings.ShowCompleteDialog = settings.ShowCompleteDialog;
        Settings.DownloadRoot = settings.DownloadRoot;
        Settings.AutoExtensions = settings.AutoExtensions;
        Settings.UserAgent = settings.UserAgent;
        Settings.Categories = settings.Categories;
        Settings.Schedule = settings.Schedule;
        Limiter.BytesPerSecond = Settings.EffectiveSpeedLimit;
        JsonFile.Save(_settingsPath, Settings);
        PumpQueue();
    }

    void Changed(DownloadItem item)
    {
        _dirty = true;
        ItemChanged?.Invoke(item);
    }

    void Tick()
    {
        List<DownloadItem> active;
        lock (_lock) active = _items.Where(i => _running.ContainsKey(i.Id)).ToList();
        var now = DateTime.UtcNow;
        foreach (var item in active)
        {
            var bytes = item.Downloaded;
            lock (_lock)
            {
                if (!_samples.TryGetValue(item.Id, out var prev)) continue;
                var seconds = (now - prev.At).TotalSeconds;
                if (seconds <= 0) continue;
                var instant = Math.Max(0, bytes - prev.Bytes) / seconds;
                // Smooth like a progress dialog should, but react quickly when a download starts.
                item.Speed = item.Speed <= 0 ? instant : item.Speed * 0.7 + instant * 0.3;
                _samples[item.Id] = (bytes, now);
            }
            ItemChanged?.Invoke(item);
        }
        if (active.Count > 0) _dirty = true;
        if (_dirty && DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(2)) Save();
    }

    public void Save()
    {
        List<DownloadItem> snapshot;
        lock (_lock)
        {
            snapshot = _items.Select(i => i.Snapshot()).ToList();
            _dirty = false;
            _lastSave = DateTime.UtcNow;
        }
        try
        {
            JsonFile.Save(_listPath, snapshot);
        }
        catch (IOException)
        {
            _dirty = true; // retry on next tick
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        List<IDownloadTask> tasks;
        lock (_lock) tasks = _running.Values.ToList();
        foreach (var t in tasks) t.Cancel();
        // Let workers record their final positions before the last save.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            lock (_lock)
                if (_running.Count == 0) break;
            Thread.Sleep(50);
        }
        foreach (var item in Items.Where(i => i.IsActive)) item.Status = DownloadStatus.Paused;
        Save();
        _http.Dispose();
    }
}
