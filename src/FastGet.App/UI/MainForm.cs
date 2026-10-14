using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FastGet.Core;

namespace FastGet.App.UI;

sealed class MainForm : Form
{
    public static readonly Icon AppIcon = Glyphs.AppIcon();

    readonly DownloadManager _m;
    readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, ShowLines = false, FullRowSelect = true, ItemHeight = 22 };
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, AllowColumnReorder = true,
    };
    readonly SplitContainer _split = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
    readonly ToolStrip _toolbar = new() { ImageScalingSize = new Size(32, 32), GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2) };
    readonly StatusStrip _statusBar = new();
    readonly ToolStripStatusLabel _statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _speedText = new();
    readonly NotifyIcon _tray = new() { Text = "FastGet Download Manager" };
    readonly FileIcons _icons = new();
    readonly Dictionary<Guid, ListViewItem> _rows = new();
    readonly Dictionary<Guid, ProgressForm> _progress = new();
    readonly ConcurrentDictionary<Guid, byte> _dirty = new();
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 250 };
    readonly bool _startHidden;
    readonly QueueScheduler _scheduler;
    ToolStripMenuItem _limiterOn = null!, _limiterOff = null!, _hideCategories = null!;
    string _filter = "all";
    int _sortColumn = -1;
    bool _sortDescending;
    bool _exiting;
    string? _lastClipboard;

    public MainForm(DownloadManager manager, bool startHidden)
    {
        _m = manager;
        _startHidden = startHidden;
        Text = "FastGet Download Manager";
        Icon = AppIcon;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.None; // sizes are scaled explicitly in OnLoad
        Size = new Size(1060, 600);
        MinimumSize = new Size(640, 360);
        StartPosition = FormStartPosition.CenterScreen;

        BuildMenu();
        BuildToolbar();
        BuildTree();
        BuildList();
        _statusBar.Items.AddRange([_statusText, _speedText]);
        _split.Panel1.Controls.Add(_tree);
        _split.Panel2.Controls.Add(_list);
        Controls.Add(_split);
        Controls.Add(_toolbar);
        Controls.Add(_statusBar);
        Controls.Add(MainMenuStrip!);

        _tray.Icon = AppIcon;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowMain();
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Items.Add("Open FastGet", null, (_, _) => ShowMain());
        _tray.ContextMenuStrip.Items.Add("Add new download...", null, (_, _) => AddUrl());
        _tray.ContextMenuStrip.Items.Add("Pause all", null, (_, _) => _m.PauseAll());
        _tray.ContextMenuStrip.Items.Add("Scheduler...", null, (_, _) => ShowScheduler());
        _tray.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => ExitApp());

        _m.ItemAdded += i => _dirty[i.Id] = 0;
        _m.ItemChanged += i => _dirty[i.Id] = 0;
        _m.ItemRemoved += i => _dirty[i.Id] = 0;
        _m.ItemCompleted += i => BeginInvokeSafe(() => OnCompleted(i));
        _m.QueueStateChanged += () => BeginInvokeSafe(() => { UpdateCommands(); UpdateStatusBar(); });
        _m.QueueCompleted += () => BeginInvokeSafe(OnQueueCompleted);
        _m.SettingsChanged += () => BeginInvokeSafe(() => { FillTree(); RebuildList(); });
        _scheduler = new QueueScheduler(_m);
        _scheduler.Started += () => BeginInvokeSafe(() =>
        {
            _m.UpdateSettings(_m.Settings.Clone()); // persist a used-up one-time schedule
            _tray.ShowBalloonTip(3000, "Scheduler", "The main download queue was started.", ToolTipIcon.Info);
        });
        _uiTimer.Tick += (_, _) => FlushDirty();
        _uiTimer.Start();

        RebuildList();
        UpdateCommands();
    }

    // ---------- layout ----------

    void BuildMenu()
    {
        var menu = new MenuStrip();
        var tasks = new ToolStripMenuItem("&Tasks");
        tasks.DropDownItems.Add(Item("&Add new download...", AddUrl, Keys.Control | Keys.N));
        tasks.DropDownItems.Add(Item("Add &batch download...", AddBatch));
        tasks.DropDownItems.Add(Item("Add batch download from &clipboard", AddFromClipboard));
        tasks.DropDownItems.Add(new ToolStripSeparator());
        var export = new ToolStripMenuItem("E&xport");
        export.DropDownItems.Add(Item("To &text file...", ExportText));
        var import = new ToolStripMenuItem("&Import");
        import.DropDownItems.Add(Item("From &text file...", ImportText));
        tasks.DropDownItems.AddRange([export, import, new ToolStripSeparator(), Item("E&xit", ExitApp)]);

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("&Download Now", ResumeSelected));
        file.DropDownItems.Add(Item("&Stop Download", StopSelected));
        file.DropDownItems.Add(Item("&Remove", () => DeleteSelected(false), Keys.Delete));
        file.DropDownItems.Add(Item("R&edownload", RedownloadSelected));

        var downloads = new ToolStripMenuItem("&Downloads");
        downloads.DropDownItems.Add(Item("&Pause All", _m.PauseAll));
        downloads.DropDownItems.Add(Item("&Delete All Completed", _m.RemoveCompleted));
        downloads.DropDownItems.Add(new ToolStripSeparator());
        downloads.DropDownItems.Add(Item("Start &queue", _m.StartQueue));
        downloads.DropDownItems.Add(Item("S&top queue", _m.StopQueue));
        downloads.DropDownItems.Add(Item("S&cheduler...", ShowScheduler));
        var limiter = new ToolStripMenuItem("Speed &Limiter");
        _limiterOn = Item("Turn o&n", () => SetLimiter(true));
        _limiterOff = Item("Turn of&f", () => SetLimiter(false));
        limiter.DropDownItems.AddRange([_limiterOn, _limiterOff, Item("S&ettings...", () => ShowOptions())]);
        downloads.DropDownItems.Add(limiter);
        downloads.DropDownItems.Add(new ToolStripSeparator());
        downloads.DropDownItems.Add(Item("&Options...", () => ShowOptions()));

        var view = new ToolStripMenuItem("&View");
        _hideCategories = Item("Hide &categories", () =>
        {
            _split.Panel1Collapsed = !_split.Panel1Collapsed;
            _hideCategories.Checked = _split.Panel1Collapsed;
        });
        view.DropDownItems.Add(_hideCategories);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("&Browser integration...", () => ShowOptions()));
        help.DropDownItems.Add(Item("&About FastGet", () => MessageBox.Show(this,
            "FastGet Download Manager 1.0\n\nMulti-connection download manager with resume, queue, categories and browser integration.",
            "About", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        menu.Items.AddRange([tasks, file, downloads, view, help]);
        MainMenuStrip = menu;
    }

    static ToolStripMenuItem Item(string text, Action action, Keys keys = Keys.None)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => action());
        if (keys != Keys.None) item.ShortcutKeys = keys;
        return item;
    }

    readonly Dictionary<string, ToolStripButton> _buttons = new();

    void BuildToolbar()
    {
        void Add(string key, string text, string glyph, Color color, Action action)
        {
            var b = new ToolStripButton(text, Glyphs.Render(glyph, color, LogicalToDeviceUnits(32)), (_, _) => action())
            {
                TextImageRelation = TextImageRelation.ImageAboveText,
                ImageScaling = ToolStripItemImageScaling.None,
                Padding = new Padding(LogicalToDeviceUnits(6), 0, LogicalToDeviceUnits(6), 0),
            };
            _buttons[key] = b;
            _toolbar.Items.Add(b);
        }
        Add("add", "Add URL", Glyphs.Add, Color.FromArgb(0, 120, 215), AddUrl);
        Add("resume", "Resume", Glyphs.Play, Color.FromArgb(16, 137, 62), ResumeSelected);
        Add("stop", "Stop", Glyphs.Stop, Color.FromArgb(196, 43, 28), StopSelected);
        Add("stopall", "Stop All", Glyphs.Pause, Color.FromArgb(150, 30, 20), _m.PauseAll);
        _toolbar.Items.Add(new ToolStripSeparator());
        Add("delete", "Delete", Glyphs.Delete, Color.DimGray, () => DeleteSelected(false));
        Add("clear", "Delete Completed", Glyphs.Clear, Color.DimGray, _m.RemoveCompleted);
        _toolbar.Items.Add(new ToolStripSeparator());
        Add("options", "Options", Glyphs.Settings, Color.FromArgb(90, 90, 90), () => ShowOptions());
        Add("batch", "Batch", Glyphs.Batch, Color.FromArgb(120, 70, 180), AddBatch);
        _toolbar.Items.Add(new ToolStripSeparator());
        Add("qstart", "Start Queue", Glyphs.Queue, Color.FromArgb(16, 137, 62), _m.StartQueue);
        Add("qstop", "Stop Queue", Glyphs.Queue, Color.FromArgb(196, 43, 28), _m.StopQueue);
        Add("scheduler", "Scheduler", Glyphs.Clock, Color.FromArgb(0, 120, 215), ShowScheduler);
    }

    void BuildTree()
    {
        int iconSize = LogicalToDeviceUnits(18);
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(iconSize, iconSize) };
        void Img(string key, string glyph, Color c) => images.Images.Add(key, Glyphs.Render(glyph, c, iconSize));
        Img("all", Glyphs.All, Color.FromArgb(0, 120, 215));
        Img("folder", Glyphs.Folder, Color.FromArgb(220, 160, 30));
        Img("Compressed", Glyphs.Package, Color.FromArgb(160, 110, 40));
        Img("Documents", Glyphs.Document, Color.FromArgb(40, 90, 170));
        Img("Music", Glyphs.Music, Color.FromArgb(200, 60, 120));
        Img("Programs", Glyphs.Apps, Color.FromArgb(70, 70, 70));
        Img("Video", Glyphs.Video, Color.FromArgb(120, 70, 180));
        Img("unfinished", Glyphs.Sync, Color.FromArgb(230, 120, 0));
        Img("finished", Glyphs.Check, Color.FromArgb(16, 137, 62));
        Img("queue", Glyphs.Queue, Color.FromArgb(0, 120, 215));
        _tree.ImageList = images;
        _tree.AfterSelect += (_, e) =>
        {
            _filter = (string)e.Node!.Tag!;
            RebuildList();
        };
        FillTree();
    }

    void FillTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        TreeNode Node(string text, string tag, string image) => new(text) { Tag = tag, ImageKey = image, SelectedImageKey = image };
        var all = Node("All Downloads", "all", "all");
        foreach (var c in _m.Settings.Categories.Where(c => c.Name != Category.GeneralName))
        {
            var key = _tree.ImageList!.Images.ContainsKey(c.Name) ? c.Name : "folder";
            all.Nodes.Add(Node(c.Name, "cat:" + c.Name, key));
        }
        var unfinished = Node("Unfinished", "unfinished", "unfinished");
        var finished = Node("Finished", "finished", "finished");
        var queues = Node("Queues", "queue", "queue");
        queues.Nodes.Add(Node("Main download queue", "queue", "queue"));
        _tree.Nodes.AddRange([all, unfinished, finished, queues]);
        all.Expand();
        queues.Expand();
        // Keep the user on the same category/filter when the tree is rebuilt.
        _tree.SelectedNode = FindNode(_tree.Nodes, _filter) ?? all;
        _tree.EndUpdate();
    }

    static TreeNode? FindNode(TreeNodeCollection nodes, string tag)
    {
        foreach (TreeNode n in nodes)
        {
            if ((string?)n.Tag == tag) return n;
            if (FindNode(n.Nodes, tag) is { } found) return found;
        }
        return null;
    }

    void BuildList()
    {
        _list.SmallImageList = _icons.List;
        foreach (var (name, width) in new[] { ("File Name", 280), ("Size", 80), ("Status", 90), ("Time left", 100),
                     ("Transfer rate", 100), ("Last Try Date", 130), ("Description", 200) })
            _list.Columns.Add(name, width);
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.SelectedIndexChanged += (_, _) => UpdateCommands();
        _list.ColumnClick += (_, e) =>
        {
            _sortDescending = _sortColumn == e.Column && !_sortDescending;
            _sortColumn = e.Column;
            RebuildList();
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) OpenSelected();
            if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem i in _list.Items) i.Selected = true;
        };

        var ctx = new ContextMenuStrip();
        ctx.Items.Add("&Open", null, (_, _) => OpenSelected());
        ctx.Items.Add("Open &folder", null, (_, _) => { if (Selected.FirstOrDefault() is { } i) Shell.ShowInFolder(File.Exists(i.FullPath) ? i.FullPath : i.PartPath); });
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("&Resume", null, (_, _) => ResumeSelected());
        ctx.Items.Add("&Stop", null, (_, _) => StopSelected());
        ctx.Items.Add("Add to &queue", null, (_, _) => { foreach (var i in Selected) _m.AddToQueue(i); });
        ctx.Items.Add("R&edownload", null, (_, _) => RedownloadSelected());
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("&Copy address", null, (_, _) => { if (Selected.FirstOrDefault() is { } i) Clipboard.SetText(i.Url); });
        ctx.Items.Add("&Delete", null, (_, _) => DeleteSelected(false));
        ctx.Items.Add("Delete with &file", null, (_, _) => DeleteSelected(true));
        _list.ContextMenuStrip = ctx;
    }

    // ---------- list ----------

    List<DownloadItem> Selected => _list.SelectedItems.Cast<ListViewItem>().Select(r => (DownloadItem)r.Tag!).ToList();

    bool Matches(DownloadItem i) => _filter switch
    {
        "all" => true,
        "unfinished" => i.Status != DownloadStatus.Completed,
        "finished" => i.Status == DownloadStatus.Completed,
        "queue" => i.InQueue,
        _ when _filter.StartsWith("cat:") => i.Category == _filter[4..],
        _ => true,
    };

    string[] Cells(DownloadItem i) =>
    [
        i.FileName,
        i.Kind == DownloadKind.Hls && i.Status != DownloadStatus.Completed
            ? i.ExpectedSize > 0 ? "~" + Fmt.Size(i.ExpectedSize) : "Stream"
            : Fmt.Size(i.TotalSize),
        Fmt.Status(i),
        Fmt.Time(i.TimeLeft),
        i.IsActive ? Fmt.Speed(i.Speed) : "",
        i.LastTry?.ToString("MMM dd yyyy HH:mm") ?? "",
        i.Status == DownloadStatus.Error ? i.Error ?? "" : i.Description,
    ];

    void RebuildList()
    {
        var selected = Selected.Select(i => i.Id).ToHashSet();
        IEnumerable<DownloadItem> items = _m.Items.Where(Matches);
        if (_sortColumn >= 0)
        {
            Func<DownloadItem, IComparable> key = _sortColumn switch
            {
                1 => i => i.TotalSize,
                2 => i => i.Status == DownloadStatus.Completed ? 2.0 : i.Progress,
                3 => i => i.TimeLeft ?? TimeSpan.MaxValue,
                4 => i => i.Speed,
                5 => i => i.LastTry ?? DateTime.MinValue,
                6 => i => i.Description,
                _ => i => i.FileName,
            };
            items = _sortDescending ? items.OrderByDescending(key) : items.OrderBy(key);
        }

        _list.BeginUpdate();
        _list.Items.Clear();
        _rows.Clear();
        foreach (var i in items)
        {
            var row = new ListViewItem(Cells(i)) { Tag = i, ImageIndex = _icons.For(i.FileName), Selected = selected.Contains(i.Id) };
            _rows[i.Id] = row;
            _list.Items.Add(row);
        }
        _list.EndUpdate();
        UpdateStatusBar();
    }

    void FlushDirty()
    {
        if (_dirty.IsEmpty) return;
        bool rebuild = false;
        foreach (var id in _dirty.Keys)
        {
            _dirty.TryRemove(id, out _);
            var item = _m.Find(id);
            bool shown = _rows.TryGetValue(id, out var row);
            if (item == null || Matches(item) != shown)
            {
                rebuild = true; // added, removed, or moved in/out of the current filter
                continue;
            }
            if (!shown) continue;
            var cells = Cells(item);
            for (int c = 0; c < cells.Length; c++)
                if (row!.SubItems[c].Text != cells[c]) row.SubItems[c].Text = cells[c];
            var img = _icons.For(item.FileName);
            if (row!.ImageIndex != img) row.ImageIndex = img;
        }
        if (rebuild) RebuildList();
        UpdateStatusBar();
        UpdateCommands();
    }

    void UpdateStatusBar()
    {
        var items = _m.Items;
        int active = items.Count(i => i.IsActive);
        _statusText.Text = $"{items.Count} downloads, {active} active" + (_m.QueueRunning ? " — queue running" : "");
        var speed = items.Where(i => i.IsActive).Sum(i => i.Speed);
        _speedText.Text = (speed > 0 ? Fmt.Speed(speed) : "") + (_m.Settings.EffectiveSpeedLimit > 0 ? $"  (limit {_m.Settings.SpeedLimitKBps} KB/s)" : "");
        var sc = _m.Settings.Schedule;
        var next = sc.NextStart(DateTime.Now);
        _statusText.Text += (next is { } n ? $" — queue starts {n:ddd HH:mm}" : "")
                            + (sc.WhenDone != WhenDoneAction.None ? $" — when done: {WhenDoneText(sc.WhenDone)}" : "");
        var tip = active > 0 ? $"FastGet — {active} active, {Fmt.Speed(speed)}" : "FastGet Download Manager";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip; // NotifyIcon text is limited to 63 characters
    }

    void UpdateCommands()
    {
        var sel = Selected;
        _buttons["resume"].Enabled = sel.Any(i => !i.IsActive && i.Status != DownloadStatus.Completed);
        _buttons["stop"].Enabled = sel.Any(i => i.IsActive || i.Status == DownloadStatus.Queued);
        _buttons["delete"].Enabled = sel.Count > 0;
        _buttons["qstart"].Enabled = !_m.QueueRunning;
        _buttons["qstop"].Enabled = _m.QueueRunning;
        _limiterOn.Checked = _m.Settings.SpeedLimitEnabled;
        _limiterOff.Checked = !_m.Settings.SpeedLimitEnabled;
    }

    // ---------- commands ----------

    void AddUrl()
    {
        string? initial = null;
        if (Clipboard.ContainsText() && Uri.TryCreate(Clipboard.GetText().Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            initial = u.ToString();
        using var dlg = new AddUrlForm(initial);
        if (dlg.ShowDialog(this) == DialogResult.OK) Confirm(new NewDownload { Url = dlg.Url }, forceDialog: true);
    }

    void AddBatch()
    {
        using var dlg = new BatchForm();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var url in dlg.Urls) _m.Add(new NewDownload { Url = url }, AddMode.Later);
        SelectQueueAndAsk();
    }

    void AddFromClipboard()
    {
        var urls = Clipboard.ContainsText()
            ? Clipboard.GetText().Split('\n', '\r', ' ', '\t').Select(s => s.Trim())
                .Where(s => Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https").Distinct().ToList()
            : [];
        if (urls.Count == 0)
        {
            MessageBox.Show(this, "The clipboard does not contain any http(s) addresses.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ShowLinks(urls.Select(u => new PageLink { Url = u }).ToList(), null, null, null);
    }

    void SelectQueueAndAsk()
    {
        if (!_m.QueueRunning && MessageBox.Show(this, "Files were added to the main download queue. Start the queue now?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            _m.StartQueue();
    }

    void ResumeSelected()
    {
        foreach (var i in Selected.Where(i => i.Status != DownloadStatus.Completed))
        {
            _m.Start(i);
            if (_m.Settings.ShowProgressDialog && Selected.Count == 1) ShowProgress(i);
        }
    }

    void StopSelected()
    {
        foreach (var i in Selected) _m.Pause(i);
    }

    void RedownloadSelected()
    {
        foreach (var i in Selected) _m.Redownload(i);
    }

    void DeleteSelected(bool withFiles)
    {
        var sel = Selected;
        if (sel.Count == 0) return;
        var question = withFiles
            ? $"Delete {sel.Count} download(s) and their files from disk?"
            : $"Remove {sel.Count} download(s) from the list?";
        if (MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var i in sel)
        {
            if (_progress.TryGetValue(i.Id, out var p)) p.Close();
            _m.Remove(i, withFiles);
        }
    }

    void OpenSelected()
    {
        if (Selected.FirstOrDefault() is not { } i) return;
        if (i.Status == DownloadStatus.Completed) Shell.Open(i.FullPath);
        else ShowProgress(i);
    }

    void SetLimiter(bool on)
    {
        var s = _m.Settings.Clone();
        s.SpeedLimitEnabled = on;
        if (on && s.SpeedLimitKBps <= 0) s.SpeedLimitKBps = 500;
        _m.UpdateSettings(s);
        UpdateCommands();
        UpdateStatusBar();
    }

    void ShowOptions()
    {
        using var dlg = new OptionsForm(_m.Settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _m.UpdateSettings(dlg.Result);
        FillTree();
        RebuildList();
        UpdateCommands();
    }

    void ExportText()
    {
        using var dlg = new SaveFileDialog { Filter = "Text files|*.txt", FileName = "downloads.txt" };
        if (dlg.ShowDialog(this) == DialogResult.OK) File.WriteAllLines(dlg.FileName, _m.Items.Select(i => i.Url));
    }

    void ImportText()
    {
        using var dlg = new OpenFileDialog { Filter = "Text files|*.txt|All files|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var links = File.ReadAllLines(dlg.FileName).Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(l => new PageLink { Url = l }).ToList();
        ShowLinks(links, null, null, null);
    }

    void ShowProgress(DownloadItem item)
    {
        if (_progress.TryGetValue(item.Id, out var existing))
        {
            existing.Activate();
            return;
        }
        var form = new ProgressForm(_m, item);
        _progress[item.Id] = form;
        form.FormClosed += (_, _) => _progress.Remove(item.Id);
        form.Show();
    }

    /// <summary>Shared path for every new URL: optional confirmation dialog, then add and start.</summary>
    void Confirm(NewDownload request, bool forceDialog = false)
    {
        if (_m.Settings.ShowStartDialog || forceDialog)
        {
            using var dlg = new DownloadInfoForm(_m, request);
            dlg.TopMost = !Visible || WindowState == FormWindowState.Minimized;
            if (dlg.ShowDialog(Visible ? this : null) != DialogResult.OK) return;
            var item = _m.Add(dlg.Result, dlg.Mode);
            if (dlg.Mode == AddMode.StartNow && _m.Settings.ShowProgressDialog) ShowProgress(item);
            if (dlg.Mode == AddMode.Later) SelectQueueAndAsk();
        }
        else
        {
            var item = _m.Add(request, AddMode.StartNow);
            if (_m.Settings.ShowProgressDialog) ShowProgress(item);
        }
    }

    void ShowLinks(List<PageLink> links, string? referer, string? cookies, string? userAgent)
    {
        using var dlg = new LinksForm(links, _m.Settings);
        dlg.TopMost = !Visible;
        if (dlg.ShowDialog(Visible ? this : null) != DialogResult.OK) return;
        foreach (var link in dlg.Selected)
            _m.Add(new NewDownload { Url = link.Url, Referer = referer, Cookies = cookies, UserAgent = userAgent, Description = link.Text ?? "" }, AddMode.Later);
        if (dlg.Selected.Count > 0) _m.StartQueue();
    }

    static string WhenDoneText(WhenDoneAction a) => a switch
    {
        WhenDoneAction.ExitApp => "exit",
        WhenDoneAction.Shutdown => "turn off computer",
        WhenDoneAction.Sleep => "sleep",
        WhenDoneAction.Hibernate => "hibernate",
        _ => "",
    };

    void ShowScheduler()
    {
        using var dlg = new ScheduleForm(_m.Settings);
        if (dlg.ShowDialog(Visible ? this : null) != DialogResult.OK) return;
        _m.UpdateSettings(dlg.Result);
        if (dlg.StartNow) _m.StartQueue();
        UpdateCommands();
        UpdateStatusBar();
    }

    void ShowVideo(BrowserMessage msg)
    {
        using var dlg = new VideoForm(_m, msg);
        dlg.TopMost = !Visible || WindowState == FormWindowState.Minimized;
        if (dlg.ShowDialog(Visible ? this : null) != DialogResult.OK || dlg.Result == null) return;
        var item = _m.Add(dlg.Result, dlg.Mode);
        if (dlg.Mode == AddMode.StartNow && _m.Settings.ShowProgressDialog) ShowProgress(item);
        if (dlg.Mode == AddMode.Later) SelectQueueAndAsk();
    }

    void OnQueueCompleted()
    {
        var sc = _m.Settings.Schedule;
        if (sc.WhenDone == WhenDoneAction.None)
        {
            _tray.ShowBalloonTip(3000, "Queue finished", "All files in the main download queue were processed.", ToolTipIcon.Info);
            return;
        }
        using (var dlg = new ShutdownForm(sc.WhenDone))
        {
            if (dlg.ShowDialog() != DialogResult.OK) return;
        }
        _m.Save(); // the list must be on disk before the machine goes down
        if (sc.WhenDone == WhenDoneAction.ExitApp)
        {
            ExitApp();
            return;
        }
        Power.Execute(sc.WhenDone, sc.ForceClose);
    }

    void OnCompleted(DownloadItem item)
    {
        _dirty[item.Id] = 0;
        FlushDirty(); // show "Complete" in the list before the dialog appears
        if (_progress.TryGetValue(item.Id, out var p)) p.Close();
        if (_m.Settings.ShowCompleteDialog && !item.InQueue)
            new CompleteForm(item).Show();
        else
            _tray.ShowBalloonTip(3000, "Download complete", item.FileName, ToolTipIcon.Info);
    }

    /// <summary>Called from the pipe server thread.</summary>
    public void HandleMessage(BrowserMessage msg) => BeginInvokeSafe(() =>
    {
        switch (msg.Type)
        {
            case "show":
                ShowMain();
                break;
            case "links" when msg.Links is { Count: > 0 }:
                ShowLinks(msg.Links, msg.Referer, msg.Cookies, msg.UserAgent);
                break;
            case "video" when msg.Streams is { Count: > 0 }:
                ShowVideo(msg);
                break;
            case "download" when msg.Url != null:
                Confirm(new NewDownload
                {
                    Url = msg.Url, FileName = string.IsNullOrWhiteSpace(msg.FileName) ? null : msg.FileName, Referer = msg.Referer,
                    Cookies = msg.Cookies, UserAgent = msg.UserAgent, Size = msg.Size ?? -1,
                });
                break;
        }
    });

    void BeginInvokeSafe(Action action)
    {
        if (!IsHandleCreated)
        {
            // Messages can arrive before the window exists (e.g. started by the browser); wait for the handle.
            HandleCreated += (_, _) => BeginInvoke(action);
            return;
        }
        if (!IsDisposed) BeginInvoke(action);
    }

    // ---------- window / tray / clipboard ----------

    void ShowMain()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void SetVisibleCore(bool value)
    {
        // Started by the browser: create the handle (for messages) but stay in the tray.
        if (_startHidden && !IsHandleCreated)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Pixel sizes that WinForms auto-scaling does not cover.
        var wanted = LogicalToDeviceUnits(new Size(1100, 620));
        var area = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(wanted.Width, area.Width), Math.Min(wanted.Height, area.Height));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        _split.SplitterDistance = LogicalToDeviceUnits(210);
        _tree.ItemHeight = LogicalToDeviceUnits(24);
        int[] widths = [280, 80, 90, 100, 100, 130, 200];
        for (int c = 0; c < widths.Length; c++) _list.Columns[c].Width = LogicalToDeviceUnits(widths[c]);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AddClipboardFormatListener(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_exiting)
        {
            // Like other download managers, closing the window keeps downloads running in the tray.
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        RemoveClipboardFormatListener(Handle);
        _tray.Visible = false;
        _tray.Dispose();
        _uiTimer.Dispose();
        _scheduler.Dispose();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_CLIPBOARDUPDATE = 0x031D;
        if (m.Msg == WM_CLIPBOARDUPDATE && _m.Settings.MonitorClipboard) BeginInvoke(CheckClipboard);
        base.WndProc(ref m);
    }

    void CheckClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText().Trim();
            if (text == _lastClipboard || text.Contains('\n')) return;
            _lastClipboard = text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
            if (!_m.Settings.IsAutoExtension(Http.FileNameFromUrl(text))) return;
            Confirm(new NewDownload { Url = text }, forceDialog: true);
        }
        catch (ExternalException)
        {
            // Another process holds the clipboard open; skip this change.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
