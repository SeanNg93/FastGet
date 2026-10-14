using FastGet.Core;

namespace FastGet.App.UI;

/// <summary>Shared look for the small dialogs.</summary>
abstract class DialogBase : Form
{
    protected DialogBase(string title, int width, int height)
    {
        Text = title;
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Dpi.ScaleOnLoad(this);
        ClientSize = new Size(width, height);
        Icon = MainForm.AppIcon;
    }

    protected static Label Lbl(string text, int x, int y, int w = 90) =>
        new() { Text = text, Location = new Point(x, y + 3), AutoSize = false, Size = new Size(w, 20) };

    protected static Button Btn(string text, int x, int y, int w = 110) =>
        new() { Text = text, Location = new Point(x, y), Size = new Size(w, 28) };
}

/// <summary>"Enter new address to download".</summary>
sealed class AddUrlForm : DialogBase
{
    readonly TextBox _url = new() { Location = new Point(84, 18), Width = 420 };

    public AddUrlForm(string? initial) : base("Enter new address to download", 520, 104)
    {
        Controls.Add(Lbl("Address:", 14, 18, 66));
        Controls.Add(_url);
        var ok = Btn("OK", 290, 60, 100);
        var cancel = Btn("Cancel", 404, 60, 100);
        ok.Click += (_, _) =>
        {
            if (!Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            {
                MessageBox.Show(this, "Please enter a valid http:// or https:// address.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
        };
        cancel.DialogResult = DialogResult.Cancel;
        Controls.AddRange([ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
        _url.Text = initial ?? "";
        Shown += (_, _) => _url.SelectAll();
    }

    public string Url => _url.Text.Trim();
}

/// <summary>"Download File Info": confirms name, category and folder while the server is probed in the background.</summary>
sealed class DownloadInfoForm : DialogBase
{
    readonly DownloadManager _manager;
    readonly NewDownload _request;
    readonly CancellationTokenSource _cts = new();
    readonly TextBox _url = new() { Location = new Point(100, 16), Width = 470, ReadOnly = true };
    readonly SaveTargetPanel _target;
    readonly TextBox _description = new() { Location = new Point(100, 150), Width = 470 };
    readonly Label _size = new() { Location = new Point(362, 53), Size = new Size(208, 20), Text = "Checking file size...", AutoEllipsis = true };

    public DownloadInfoForm(DownloadManager manager, NewDownload request) : base("Download File Info", 586, 232)
    {
        _manager = manager;
        _request = request;
        _target = new SaveTargetPanel(manager) { Location = new Point(14, 50) };
        Controls.AddRange([Lbl("URL", 14, 16), _url, _size, _target, Lbl("Description", 14, 150), _description]);
        _size.BringToFront();

        var start = Btn("&Start Download", 214, 190, 120);
        var later = Btn("Download &Later", 340, 190, 120);
        var cancel = Btn("&Cancel", 466, 190, 104);
        start.Click += (_, _) => Accept(AddMode.StartNow);
        later.Click += (_, _) => Accept(AddMode.Later);
        cancel.DialogResult = DialogResult.Cancel;
        Controls.AddRange([start, later, cancel]);
        AcceptButton = start;
        CancelButton = cancel;

        _url.Text = request.Url;
        _description.Text = request.Description;
        _target.SetFile(request.FileName ?? Http.FileNameFromUrl(request.Url));
        if (request.Size > 0) _size.Text = Fmt.Size(request.Size);

        FormClosed += (_, _) => _cts.Cancel();
        Shown += async (_, _) => await ProbeAsync();
    }

    public AddMode Mode { get; private set; }
    public NewDownload Result => _request;

    async Task ProbeAsync()
    {
        try
        {
            var probe = await _manager.ProbeAsync(_request.Url, _request.Referer, _request.Cookies, _request.UserAgent, _cts.Token);
            if (IsDisposed) return;
            _request.Size = probe.Size;
            _request.Resumable = probe.Resumable;
            _size.Text = (probe.Size >= 0 ? Fmt.Size(probe.Size) : "Unknown size") + (probe.Resumable ? "" : "  (no resume)");
            // A browser-supplied name wins; otherwise the server's Content-Disposition name is better than the URL.
            if (!_target.UserEditedPath && string.IsNullOrEmpty(_request.FileName)) _target.SetFile(probe.FileName);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed) _size.Text = "Could not check: " + ex.Message;
        }
    }

    void Accept(AddMode mode)
    {
        if (!_target.Commit(this)) return;
        _request.FileName = Path.GetFileName(_target.FullPath);
        _request.Folder = Path.GetDirectoryName(_target.FullPath);
        _request.Category = _target.CategoryName;
        _request.Description = _description.Text;
        Mode = mode;
        DialogResult = DialogResult.OK;
    }
}

/// <summary>Batch download: expands a wildcard address like http://site/img*.jpg into a numbered series.</summary>
sealed class BatchForm : DialogBase
{
    readonly TextBox _address = new() { Location = new Point(100, 16), Width = 420 };
    readonly RadioButton _numbers = new() { Text = "Numbers", Location = new Point(100, 50), Checked = true, AutoSize = true };
    readonly RadioButton _letters = new() { Text = "Letters", Location = new Point(200, 50), AutoSize = true };
    readonly TextBox _from = new() { Location = new Point(100, 82), Width = 70, Text = "1" };
    readonly TextBox _to = new() { Location = new Point(230, 82), Width = 70, Text = "10" };
    readonly NumericUpDown _width = new() { Location = new Point(440, 82), Width = 80, Minimum = 1, Maximum = 10, Value = 1 };
    readonly Label _preview = new() { Location = new Point(14, 118), Size = new Size(506, 60), ForeColor = Color.DimGray };

    public BatchForm() : base("Batch download", 536, 230)
    {
        Controls.AddRange([Lbl("Address", 14, 16), _address, Lbl("Replace * with", 14, 50), _numbers, _letters,
            Lbl("From", 14, 82), _from, Lbl("To", 190, 82, 36), _to, Lbl("Wildcard size", 330, 82, 106), _width, _preview]);
        var ok = Btn("OK", 300, 190, 104);
        var cancel = Btn("Cancel", 416, 190, 104);
        ok.Click += (_, _) =>
        {
            if (Urls.Count == 0)
            {
                MessageBox.Show(this, "The address must contain one * and a valid range.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
        };
        cancel.DialogResult = DialogResult.Cancel;
        Controls.AddRange([ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
        _address.Text = "http://www.example.com/pictures/img*.jpg";
        foreach (Control c in new Control[] { _address, _from, _to, _width, _numbers, _letters })
            c.TextChanged += (_, _) => RefreshPreview();
        _numbers.CheckedChanged += (_, _) =>
        {
            (_from.Text, _to.Text) = _numbers.Checked ? ("1", "10") : ("a", "z");
            RefreshPreview();
        };
        _width.ValueChanged += (_, _) => RefreshPreview();
        RefreshPreview();
    }

    public List<string> Urls
    {
        get
        {
            var addr = _address.Text.Trim();
            if (addr.Count(c => c == '*') != 1 || !Uri.TryCreate(addr.Replace("*", "1"), UriKind.Absolute, out _)) return [];
            var values = new List<string>();
            if (_numbers.Checked)
            {
                if (!int.TryParse(_from.Text, out var a) || !int.TryParse(_to.Text, out var b) || b < a || b - a > 10000) return [];
                for (int i = a; i <= b; i++) values.Add(i.ToString().PadLeft((int)_width.Value, '0'));
            }
            else
            {
                if (_from.Text.Length != 1 || _to.Text.Length != 1 || _to.Text[0] < _from.Text[0]) return [];
                for (char ch = _from.Text[0]; ch <= _to.Text[0]; ch++) values.Add(ch.ToString());
            }
            return values.Select(v => addr.Replace("*", v)).ToList();
        }
    }

    void RefreshPreview()
    {
        var urls = Urls;
        _preview.Text = urls.Count == 0
            ? "Use one * as the wildcard for the changing part of the address."
            : $"{urls.Count} files\nFirst: {urls[0]}\nLast:  {urls[^1]}";
    }
}

/// <summary>"Download all links": pick which links from a page to add to the main queue.</summary>
sealed class LinksForm : DialogBase
{
    readonly ListView _list = new()
    {
        Location = new Point(12, 40), Size = new Size(676, 330), View = View.Details, CheckBoxes = true, FullRowSelect = true,
    };
    readonly CheckBox _hideHtml = new() { Text = "Hide HTML pages", Location = new Point(12, 380), AutoSize = true, Checked = true };
    readonly List<PageLink> _links;
    readonly AppSettings _settings;

    public LinksForm(List<PageLink> links, AppSettings settings) : base("Download all links", 700, 420)
    {
        _settings = settings;
        _links = links.Where(l => Uri.TryCreate(l.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            .DistinctBy(l => l.Url).ToList();
        Controls.Add(new Label { Text = "Check the links to add to the download list. Files matching your file types are pre-checked.", Location = new Point(12, 12), AutoSize = true });
        _list.Columns.Add("File name", 200);
        _list.Columns.Add("Address", 300);
        _list.Columns.Add("Link text", 150);
        var all = Btn("Check all", 180, 376, 90);
        var none = Btn("Uncheck all", 276, 376, 90);
        var ok = Btn("OK", 480, 376, 100);
        var cancel = Btn("Cancel", 588, 376, 100);
        all.Click += (_, _) => SetAll(true);
        none.Click += (_, _) => SetAll(false);
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        _hideHtml.CheckedChanged += (_, _) => Fill();
        Controls.AddRange([_list, _hideHtml, all, none, ok, cancel]);
        CancelButton = cancel;
        Fill();
    }

    public List<PageLink> Selected => _list.CheckedItems.Cast<ListViewItem>().Select(i => (PageLink)i.Tag!).ToList();

    void Fill()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var link in _links)
        {
            var name = Http.FileNameFromUrl(link.Url);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            bool isPage = ext is "" or ".html" or ".htm" or ".php" or ".asp" or ".aspx" or ".jsp";
            if (_hideHtml.Checked && isPage) continue;
            var row = new ListViewItem([name, link.Url, link.Text ?? ""]) { Tag = link, Checked = _settings.IsAutoExtension(name) };
            _list.Items.Add(row);
        }
        _list.EndUpdate();
    }

    void SetAll(bool value)
    {
        foreach (ListViewItem i in _list.Items) i.Checked = value;
    }
}

/// <summary>Shown when a download finishes.</summary>
sealed class CompleteForm : DialogBase
{
    public CompleteForm(DownloadItem item) : base("Download complete", 520, 170)
    {
        Controls.Add(new Label { Text = "Download complete", Font = new Font("Segoe UI", 12f, FontStyle.Bold), Location = new Point(14, 12), AutoSize = true });
        Controls.Add(Lbl("Address", 14, 48, 80));
        Controls.Add(new TextBox { Text = item.Url, ReadOnly = true, Location = new Point(96, 48), Width = 410 });
        Controls.Add(Lbl("Saved to", 14, 78, 80));
        Controls.Add(new TextBox { Text = item.FullPath, ReadOnly = true, Location = new Point(96, 78), Width = 410 });
        Controls.Add(new Label { Text = Fmt.Size(item.TotalSize), Location = new Point(96, 106), AutoSize = true, ForeColor = Color.DimGray });
        var open = Btn("&Open", 176, 128, 104);
        var folder = Btn("Open &folder", 288, 128, 104);
        var close = Btn("&Close", 400, 128, 104);
        open.Click += (_, _) => { Shell.Open(item.FullPath); Close(); };
        folder.Click += (_, _) => { Shell.ShowInFolder(item.FullPath); Close(); };
        close.Click += (_, _) => Close();
        Controls.AddRange([open, folder, close]);
        AcceptButton = open;
        CancelButton = close;
        ShowInTaskbar = true;
        TopMost = true;
    }
}

static class Shell
{
    public static void Open(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "FastGet", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    public static void ShowInFolder(string path)
    {
        if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(Path.GetDirectoryName(path))) Open(Path.GetDirectoryName(path)!);
    }
}
