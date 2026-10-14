using System.Diagnostics;
using System.Runtime.InteropServices;
using FastGet.Core;

namespace FastGet.App.UI;

/// <summary>Pick the quality of a video captured by the browser extension.</summary>
sealed class VideoForm : DialogBase
{
    readonly DownloadManager _manager;
    readonly BrowserMessage _message;
    readonly CancellationTokenSource _cts = new();
    readonly ListView _options = new()
    {
        Location = new Point(14, 62), Size = new Size(612, 170), View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = false,
    };
    readonly Label _status = new() { Location = new Point(14, 238), Size = new Size(612, 20), ForeColor = Color.DimGray };
    readonly TextBox _name = new() { Location = new Point(100, 266), Width = 526 };
    readonly TextBox _folder = new() { Location = new Point(100, 298), Width = 486 };
    readonly Button _browse = new() { Text = "...", Location = new Point(592, 297), Size = new Size(34, 25) };
    readonly Button _start;
    readonly Button _later;
    readonly string _baseName;

    public VideoForm(DownloadManager manager, BrowserMessage message) : base("Download video", 640, 380)
    {
        _manager = manager;
        _message = message;
        var title = string.IsNullOrWhiteSpace(message.Title) ? "video" : message.Title.Trim();
        _baseName = Http.Sanitize(title.Length > 120 ? title[..120] : title);

        Controls.Add(new Label { Text = title, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Location = new Point(14, 12), Size = new Size(612, 22), AutoEllipsis = true });
        Controls.Add(new Label { Text = "Streams captured while the page played. Choose a quality:", Location = new Point(14, 38), AutoSize = true });
        _options.Columns.Add("Quality / type", 330);
        _options.Columns.Add("Size", 90);
        _options.Columns.Add("Address", 300);
        Controls.AddRange([_options, _status, Lbl("File name", 14, 266), _name, Lbl("Save to", 14, 298), _folder, _browse]);

        _start = Btn("&Start Download", 262, 340, 120);
        _later = Btn("Download &Later", 388, 340, 120);
        var cancel = Btn("&Cancel", 514, 340, 112);
        _start.Click += (_, _) => Accept(AddMode.StartNow);
        _later.Click += (_, _) => Accept(AddMode.Later);
        cancel.DialogResult = DialogResult.Cancel;
        Controls.AddRange([_start, _later, cancel]);
        AcceptButton = _start;
        CancelButton = cancel;
        _start.Enabled = _later.Enabled = false;

        var video = manager.Settings.Categories.FirstOrDefault(c => c.Name == "Video");
        _folder.Text = video?.Folder ?? manager.Settings.DownloadRoot;
        _name.Text = _baseName + ".mp4";
        _options.SelectedIndexChanged += (_, _) => UpdateName();
        _options.DoubleClick += (_, _) => Accept(AddMode.StartNow);
        _browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _folder.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
        };
        FormClosed += (_, _) => _cts.Cancel();
        Shown += async (_, _) => await InspectAllAsync();
    }

    public NewDownload? Result { get; private set; }
    public AddMode Mode { get; private set; }

    async Task InspectAllAsync()
    {
        var streams = _message.Streams ?? [];
        _status.Text = $"Checking {streams.Count} captured stream(s)...";
        var seen = new HashSet<string>();
        var errors = new List<string>();
        foreach (var s in streams)
        {
            try
            {
                var options = await _manager.InspectStreamAsync(s.Url, _message.Referer, _message.Cookies, _message.UserAgent, _cts.Token);
                if (IsDisposed) return;
                foreach (var o in options.Where(o => seen.Add(o.Url)))
                {
                    var size = o.Size > 0 ? Fmt.Size(o.Size) : s.Size is > 0 ? Fmt.Size(s.Size.Value) : o.Kind == DownloadKind.Hls ? "stream" : "";
                    _options.Items.Add(new ListViewItem([o.Label, size, o.Url]) { Tag = o });
                }
                if (_options.SelectedItems.Count == 0 && _options.Items.Count > 0) _options.Items[0].Selected = true;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }
        if (IsDisposed) return;
        _status.Text = _options.Items.Count == 0
            ? "No downloadable stream found." + (errors.Count > 0 ? " " + errors[0] : "")
            : $"{_options.Items.Count} option(s). The first one is the best quality found." + (errors.Count > 0 ? $" ({errors.Count} could not be read)" : "");
        _start.Enabled = _later.Enabled = _options.Items.Count > 0;
    }

    void UpdateName()
    {
        if (_options.SelectedItems.Count == 0) return;
        var o = (StreamOption)_options.SelectedItems[0].Tag!;
        var ext = string.IsNullOrEmpty(o.Extension) ? ".mp4" : o.Extension;
        _name.Text = Path.GetFileNameWithoutExtension(_name.Text) + ext;
    }

    void Accept(AddMode mode)
    {
        if (_options.SelectedItems.Count == 0) return;
        var o = (StreamOption)_options.SelectedItems[0].Tag!;
        Result = new NewDownload
        {
            Kind = o.Kind,
            Url = o.Url,
            FileName = Http.Sanitize(_name.Text.Trim()),
            Folder = _folder.Text.Trim(),
            Category = "Video",
            Description = _message.Title ?? "",
            Referer = _message.Referer,
            Cookies = _message.Cookies,
            UserAgent = _message.UserAgent,
            Size = o.Size,
        };
        Mode = mode;
        DialogResult = DialogResult.OK;
    }
}

/// <summary>Scheduler for the main download queue.</summary>
sealed class ScheduleForm : DialogBase
{
    readonly AppSettings _s;
    readonly CheckBox _startOn = new() { Text = "Start download at", Location = new Point(14, 18), AutoSize = true };
    readonly DateTimePicker _startTime = TimePicker(170, 16);
    readonly RadioButton _once = new() { Text = "Once on", Location = new Point(34, 50), AutoSize = true };
    readonly DateTimePicker _onceDate = new() { Location = new Point(170, 48), Width = 160, Format = DateTimePickerFormat.Short };
    readonly RadioButton _daily = new() { Text = "Daily on", Location = new Point(34, 82), AutoSize = true };
    readonly CheckBox[] _days;
    readonly CheckBox _stopOn = new() { Text = "Stop download at", Location = new Point(14, 152), AutoSize = true };
    readonly DateTimePicker _stopTime = TimePicker(170, 150);
    readonly NumericUpDown _retries = new() { Location = new Point(340, 188), Width = 70, Maximum = 100 };
    readonly NumericUpDown _concurrent = new() { Location = new Point(340, 220), Width = 70, Minimum = 1, Maximum = 20 };
    readonly ComboBox _whenDone = new() { Location = new Point(170, 270), Width = 240, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox _force = new() { Text = "Force programs to close (may lose unsaved work)", Location = new Point(14, 302), AutoSize = true };
    readonly Label _next = new() { Location = new Point(14, 338), Size = new Size(560, 20), ForeColor = Color.DimGray };

    static readonly (WhenDoneAction Action, string Text)[] Actions =
    [
        (WhenDoneAction.None, "Do nothing"),
        (WhenDoneAction.ExitApp, "Exit FastGet"),
        (WhenDoneAction.Shutdown, "Turn off computer"),
        (WhenDoneAction.Sleep, "Sleep"),
        (WhenDoneAction.Hibernate, "Hibernate"),
    ];

    public ScheduleForm(AppSettings settings) : base("Scheduler — Main download queue", 580, 410)
    {
        _s = settings.Clone();
        var sc = _s.Schedule;
        _days = Enum.GetValues<DayOfWeek>().Select((d, i) => new CheckBox
        {
            Text = d.ToString()[..3], Location = new Point(170 + (i % 4) * 82, 82 + (i / 4) * 28), AutoSize = true, Tag = d,
            Checked = sc.Days.Contains(d),
        }).ToArray();

        _startOn.Checked = sc.StartEnabled;
        _startTime.Value = DateTime.Today + (ScheduleSettings.TryParseTime(sc.StartTime, out var st) ? st : TimeSpan.FromHours(2));
        _once.Checked = !sc.Daily;
        _daily.Checked = sc.Daily;
        _onceDate.Value = DateTime.TryParse(sc.OnceDate, out var od) && od >= DateTime.Today ? od : DateTime.Today;
        _stopOn.Checked = sc.StopEnabled;
        _stopTime.Value = DateTime.Today + (ScheduleSettings.TryParseTime(sc.StopTime, out var et) ? et : TimeSpan.FromHours(7));
        _retries.Value = sc.QueueRetries;
        _concurrent.Value = _s.MaxConcurrent;
        foreach (var a in Actions) _whenDone.Items.Add(a.Text);
        _whenDone.SelectedIndex = Array.FindIndex(Actions, a => a.Action == sc.WhenDone);
        _force.Checked = sc.ForceClose;

        Controls.AddRange([_startOn, _startTime, _once, _onceDate, _daily, .. _days, _stopOn, _stopTime,
            Lbl("Retries for each file if downloading failed", 14, 188, 320), _retries,
            Lbl("Download files at the same time", 14, 220, 320), _concurrent,
            Lbl("When the queue is finished", 14, 270, 156), _whenDone, _force, _next]);

        var startNow = Btn("Start queue &now", 14, 370, 130);
        var ok = Btn("OK", 352, 370, 104);
        var cancel = Btn("Cancel", 462, 370, 104);
        startNow.Click += (_, _) => { StartNow = true; Accept(); };
        ok.Click += (_, _) => Accept();
        cancel.DialogResult = DialogResult.Cancel;
        Controls.AddRange([startNow, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;

        foreach (var c in new Control[] { _startOn, _startTime, _once, _onceDate, _daily, _stopOn, _stopTime }.Concat(_days))
        {
            if (c is CheckBox cb) cb.CheckedChanged += (_, _) => Refresh();
            else if (c is RadioButton rb) rb.CheckedChanged += (_, _) => Refresh();
            else if (c is DateTimePicker dp) dp.ValueChanged += (_, _) => Refresh();
        }
        _whenDone.SelectedIndexChanged += (_, _) => _force.Enabled = Actions[_whenDone.SelectedIndex].Action != WhenDoneAction.None;
        Refresh();
    }

    public AppSettings Result => _s;
    public bool StartNow { get; private set; }

    static DateTimePicker TimePicker(int x, int y) =>
        new() { Location = new Point(x, y), Width = 100, Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true };

    new void Refresh()
    {
        _startTime.Enabled = _once.Enabled = _daily.Enabled = _startOn.Checked;
        _onceDate.Enabled = _startOn.Checked && _once.Checked;
        foreach (var d in _days) d.Enabled = _startOn.Checked && _daily.Checked;
        _stopTime.Enabled = _stopOn.Checked;
        _force.Enabled = _whenDone.SelectedIndex > 0;
        Collect();
        var next = _s.Schedule.NextStart(DateTime.Now);
        _next.Text = next is { } n ? $"Next start: {n:dddd, dd MMM yyyy HH:mm}" : _startOn.Checked ? "No upcoming start with these settings." : "";
    }

    void Collect()
    {
        var sc = _s.Schedule;
        sc.StartEnabled = _startOn.Checked;
        sc.StartTime = _startTime.Value.ToString("HH:mm");
        sc.Daily = _daily.Checked;
        sc.OnceDate = _onceDate.Value.ToString("yyyy-MM-dd");
        sc.Days = _days.Where(d => d.Checked).Select(d => (DayOfWeek)d.Tag!).ToList();
        sc.StopEnabled = _stopOn.Checked;
        sc.StopTime = _stopTime.Value.ToString("HH:mm");
        sc.QueueRetries = (int)_retries.Value;
        sc.WhenDone = Actions[Math.Max(0, _whenDone.SelectedIndex)].Action;
        sc.ForceClose = _force.Checked;
        _s.MaxConcurrent = (int)_concurrent.Value;
    }

    void Accept()
    {
        Collect();
        if (_s.Schedule.StartEnabled && _s.Schedule.Daily && _s.Schedule.Days.Count == 0)
        {
            MessageBox.Show(this, "Choose at least one day.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
    }
}

/// <summary>Countdown before the computer is shut down (or put to sleep) at the end of the queue.</summary>
sealed class ShutdownForm : DialogBase
{
    const int Seconds = 30;
    readonly ProgressBar _bar = new() { Location = new Point(14, 76), Size = new Size(472, 18), Maximum = Seconds };
    readonly Label _countdown = new() { Location = new Point(14, 100), Size = new Size(472, 20) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly string _verb;
    int _left = Seconds;

    public ShutdownForm(WhenDoneAction action) : base("Queue finished", 500, 170)
    {
        _verb = action switch
        {
            WhenDoneAction.Shutdown => "shutting down",
            WhenDoneAction.Sleep => "going to sleep",
            WhenDoneAction.Hibernate => "hibernating",
            _ => "closing FastGet",
        };
        Controls.Add(new Label
        {
            Text = "FastGet has finished processing the queue. According to the scheduler settings " +
                   (action == WhenDoneAction.ExitApp ? "FastGet will now close." : $"your computer is {_verb}."),
            Location = new Point(14, 14), Size = new Size(472, 54),
        });
        Controls.AddRange([_bar, _countdown]);
        var now = Btn("Do it &now", 266, 130, 104);
        var cancel = Btn("&Cancel", 382, 130, 104);
        now.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        Controls.AddRange([now, cancel]);
        CancelButton = cancel;
        TopMost = true;
        ShowInTaskbar = true;
        _timer.Tick += (_, _) =>
        {
            _left--;
            Tick();
            if (_left <= 0)
            {
                _timer.Stop();
                DialogResult = DialogResult.OK;
            }
        };
        Shown += (_, _) => { Tick(); _timer.Start(); };
        FormClosed += (_, _) => _timer.Dispose();
    }

    void Tick()
    {
        _bar.Value = Seconds - Math.Max(0, _left);
        _countdown.Text = $"{char.ToUpper(_verb[0])}{_verb[1..]} in {_left} seconds...";
    }
}

static class Power
{
    public static void Execute(WhenDoneAction action, bool force)
    {
        switch (action)
        {
            case WhenDoneAction.Shutdown:
                Process.Start(new ProcessStartInfo("shutdown.exe", force ? "/s /t 0 /f" : "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                break;
            case WhenDoneAction.Sleep:
                SetSuspendState(false, force, false);
                break;
            case WhenDoneAction.Hibernate:
                SetSuspendState(true, force, false);
                break;
        }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
