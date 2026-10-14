using FastGet.Core;

namespace FastGet.App.UI;

/// <summary>Per-download progress window with connection details.</summary>
sealed class ProgressForm : Form
{
    readonly DownloadManager _manager;
    readonly DownloadItem _item;
    readonly Label _url = Value(), _status = Value(), _size = Value(), _downloaded = Value(), _rate = Value(), _left = Value(), _resume = Value();
    readonly SmoothProgressBar _bar = new() { Location = new Point(14, 196), Size = new Size(536, 20) };
    readonly Button _details = new() { Text = "<< Hide &details", Location = new Point(14, 228), Size = new Size(130, 28) };
    readonly Button _pause = new() { Text = "&Pause", Location = new Point(334, 228), Size = new Size(104, 28) };
    readonly Button _cancel = new() { Text = "&Cancel", Location = new Point(446, 228), Size = new Size(104, 28) };
    readonly Panel _detailPanel = new() { Location = new Point(0, 266), Size = new Size(564, 210) };
    readonly SegmentBar _segments = new() { Location = new Point(14, 24), Width = 536 };
    readonly ListView _connections = new()
    {
        Location = new Point(14, 54), Size = new Size(536, 146), View = View.Details, FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    /// <summary>Text fields: twice a second is readable.</summary>
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    /// <summary>Bars: every frame (~60 fps; WinForms timers tick at most every ~15 ms).</summary>
    readonly System.Windows.Forms.Timer _frame = new() { Interval = 15 };
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    TimeSpan _lastFrame;

    public ProgressForm(DownloadManager manager, DownloadItem item)
    {
        _manager = manager;
        _item = item;
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Dpi.ScaleOnLoad(this);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(564, 476);
        Icon = MainForm.AppIcon;

        int y = 14;
        foreach (var (caption, value) in new[] { ("URL", _url), ("Status", _status), ("File size", _size), ("Downloaded", _downloaded),
                     ("Transfer rate", _rate), ("Time left", _left), ("Resume capability", _resume) })
        {
            Controls.Add(new Label { Text = caption, Location = new Point(14, y), Size = new Size(120, 20) });
            value.Location = new Point(140, y);
            Controls.Add(value);
            y += 25;
        }
        _url.Text = item.Url;
        _url.AutoEllipsis = true;
        Controls.AddRange([_bar, _details, _pause, _cancel, _detailPanel]);

        _detailPanel.Controls.Add(new Label { Text = "Start positions and download progress by connections", Location = new Point(14, 0), AutoSize = true });
        _detailPanel.Controls.Add(_segments);
        _connections.Columns.Add("N.", 40);
        _connections.Columns.Add("Downloaded", 120);
        _connections.Columns.Add("Info", 350);
        _detailPanel.Controls.Add(_connections);

        _details.Click += (_, _) => ToggleDetails();
        _pause.Click += (_, _) =>
        {
            if (_item.IsActive) _manager.Pause(_item);
            else _manager.Start(_item);
        };
        _cancel.Click += (_, _) =>
        {
            _manager.Pause(_item);
            Close();
        };
        _timer.Tick += (_, _) => RefreshView();
        _timer.Start();
        _frame.Tick += (_, _) => RefreshBars();
        _frame.Start();
        FormClosed += (_, _) =>
        {
            _timer.Dispose();
            _frame.Dispose();
        };
        RefreshView();
        RefreshBars();
    }

    public Guid ItemId => _item.Id;

    static Label Value() => new() { Size = new Size(410, 20), AutoEllipsis = true };

    void ToggleDetails()
    {
        bool show = !_detailPanel.Visible;
        _detailPanel.Visible = show;
        _details.Text = show ? "<< Hide &details" : "Show &details >>";
        ClientSize = new Size(ClientSize.Width, show ? _detailPanel.Bottom : _detailPanel.Top);
    }

    void RefreshView()
    {
        var pct = _item.Progress * 100;
        bool hls = _item.Kind == DownloadKind.Hls;
        Text = _item.HasKnownProgress ? $"{pct:0}% {_item.FileName}" : _item.FileName;
        _status.Text = _item.Status switch
        {
            DownloadStatus.Downloading => "Receiving data...",
            DownloadStatus.Connecting => "Connecting...",
            DownloadStatus.Error => "Error: " + _item.Error,
            DownloadStatus.Completed => "Complete",
            DownloadStatus.Queued => "Queued",
            _ => "Paused",
        };
        _status.ForeColor = _item.Status == DownloadStatus.Error ? Color.Firebrick : SystemColors.ControlText;
        _size.Text = hls && _item.Status != DownloadStatus.Completed
            ? _item.ExpectedSize > 0 ? $"~{Fmt.Size(_item.ExpectedSize)} (estimated)" : "Video stream"
            : _item.TotalSize >= 0 ? Fmt.Size(_item.TotalSize) : "Unknown";
        _downloaded.Text = Fmt.Size(_item.Downloaded) + (hls
            ? $" ( {_item.StreamDone} / {_item.StreamTotal} segments )"
            : _item.TotalSize > 0 ? $" ( {pct:0.000} % )" : "");
        _rate.Text = Fmt.Speed(_item.Speed);
        _left.Text = Fmt.Time(_item.TimeLeft);
        _resume.Text = hls ? "Yes (by segment)" : _item.TotalSize < 0 && !_item.Resumable && _item.Status == DownloadStatus.Connecting ? "" : _item.Resumable ? "Yes" : "No";
        _pause.Text = _item.IsActive ? "&Pause" : "&Resume";
        _pause.Enabled = _item.Status != DownloadStatus.Completed;

        var segments = _item.SnapshotSegments();
        if (hls) segments = HlsSegments();
        _connections.BeginUpdate();
        while (_connections.Items.Count > segments.Count) _connections.Items.RemoveAt(_connections.Items.Count - 1);
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            string[] cells = [(i + 1).ToString(), Fmt.Size(s.Downloaded), s.IsDone ? "Done" : s.Active ? s.Info : ""];
            if (i < _connections.Items.Count)
            {
                var row = _connections.Items[i];
                for (int c = 0; c < cells.Length; c++)
                    if (row.SubItems[c].Text != cells[c]) row.SubItems[c].Text = cells[c];
            }
            else
            {
                _connections.Items.Add(new ListViewItem(cells));
            }
        }
        _connections.EndUpdate();

        if (_item.Status == DownloadStatus.Completed) Close();
    }

    // Streams have no byte ranges; show finished segments as one bar instead.
    List<Segment> HlsSegments() =>
        _item.StreamTotal > 0 ? [new Segment { Start = 0, End = _item.StreamTotal - 1, Downloaded = _item.StreamDone }] : [];

    /// <summary>Runs every frame with the live byte counts, so the bars move continuously.</summary>
    void RefreshBars()
    {
        var now = _clock.Elapsed;
        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        _bar.Indeterminate = _item.IsActive && !_item.HasKnownProgress;
        _bar.Target = _item.Progress;
        _bar.Step(dt);
        bool hls = _item.Kind == DownloadKind.Hls;
        if (_detailPanel.Visible)
            _segments.SetSegments(hls ? HlsSegments() : _item.SnapshotSegments(), hls ? _item.StreamTotal : _item.TotalSize);
    }
}
