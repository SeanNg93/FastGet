using FastGet.Core;

namespace FastGet.App.UI;

sealed class OptionsForm : Form
{
    readonly AppSettings _s;
    readonly CheckBox _clipboard = Check("Automatically start downloading of URLs placed to clipboard", 16);
    readonly CheckBox _startDialog = Check("Show \"Download File Info\" dialog before downloading", 44);
    readonly CheckBox _progressDialog = Check("Show download progress dialog", 72);
    readonly CheckBox _completeDialog = Check("Show download complete dialog", 100);
    readonly Label _integrationStatus = new() { Location = new Point(16, 176), Size = new Size(500, 20) };
    readonly NumericUpDown _connections = Number(1, 32, 16);
    readonly NumericUpDown _concurrent = Number(1, 20, 50);
    readonly NumericUpDown _retries = Number(0, 100, 84);
    readonly CheckBox _limitOn = new() { Text = "Limit total speed to", Location = new Point(16, 120), AutoSize = true };
    readonly NumericUpDown _limit = new() { Location = new Point(240, 118), Width = 90, Minimum = 1, Maximum = 1_000_000 };
    readonly TextBox _root = new() { Location = new Point(16, 36), Width = 420 };
    readonly DataGridView _categories = new()
    {
        Location = new Point(16, 100), Size = new Size(500, 220), AllowUserToAddRows = true, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window,
    };
    readonly TextBox _extensions = new() { Location = new Point(16, 36), Size = new Size(500, 120), Multiline = true, ScrollBars = ScrollBars.Vertical };
    readonly TextBox _userAgent = new() { Location = new Point(16, 196), Width = 500 };

    public OptionsForm(AppSettings settings)
    {
        _s = settings.Clone();
        Text = "Options";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Dpi.ScaleOnLoad(this);
        ClientSize = new Size(560, 420);
        Icon = MainForm.AppIcon;

        var tabs = new TabControl { Location = new Point(8, 8), Size = new Size(544, 366) };
        tabs.TabPages.Add(GeneralTab());
        tabs.TabPages.Add(ConnectionTab());
        tabs.TabPages.Add(SaveToTab());
        tabs.TabPages.Add(FileTypesTab());
        var ok = new Button { Text = "OK", Location = new Point(334, 382), Size = new Size(100, 28) };
        var cancel = new Button { Text = "Cancel", Location = new Point(444, 382), Size = new Size(100, 28), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            Collect();
            DialogResult = DialogResult.OK;
        };
        Controls.AddRange([tabs, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public AppSettings Result => _s;

    static CheckBox Check(string text, int y) => new() { Text = text, Location = new Point(16, y), AutoSize = true };
    static NumericUpDown Number(int min, int max, int y) => new() { Location = new Point(240, y), Width = 90, Minimum = min, Maximum = max };
    static Label Lbl(string text, int x, int y) => new() { Text = text, Location = new Point(x, y + 2), AutoSize = true };

    TabPage GeneralTab()
    {
        var page = new TabPage("General");
        _clipboard.Checked = _s.MonitorClipboard;
        _startDialog.Checked = _s.ShowStartDialog;
        _progressDialog.Checked = _s.ShowProgressDialog;
        _completeDialog.Checked = _s.ShowCompleteDialog;

        var group = new GroupBox { Text = "Browser integration (Chrome, Edge, Brave, Firefox)", Location = new Point(8, 136), Size = new Size(520, 190) };
        _integrationStatus.Location = new Point(12, 24);
        var install = new Button { Text = "Install", Location = new Point(12, 50), Size = new Size(100, 28) };
        var uninstall = new Button { Text = "Uninstall", Location = new Point(120, 50), Size = new Size(100, 28) };
        var openExt = new Button { Text = "Open extension folder", Location = new Point(228, 50), Size = new Size(170, 28) };
        var help = new Label
        {
            Location = new Point(12, 88), Size = new Size(496, 96), ForeColor = Color.DimGray,
            Text = "1. Click Install to register FastGet with all supported browsers.\n" +
                   "2. Chrome/Edge/Brave: open chrome://extensions, turn on Developer mode,\n" +
                   "    click \"Load unpacked\" and choose the \"chrome\" folder.\n" +
                   "3. Firefox: about:debugging → This Firefox → \"Load Temporary Add-on\",\n" +
                   "    pick firefox\\manifest.json (a signed .xpi is needed for permanent use).",
        };
        install.Click += (_, _) => { BrowserIntegration.Install(); RefreshIntegration(); };
        uninstall.Click += (_, _) => { BrowserIntegration.Uninstall(); RefreshIntegration(); };
        openExt.Click += (_, _) => Shell.Open(BrowserIntegration.ExtensionFolder);
        group.Controls.AddRange([_integrationStatus, install, uninstall, openExt, help]);
        RefreshIntegration();

        page.Controls.AddRange([_clipboard, _startDialog, _progressDialog, _completeDialog, group]);
        return page;
    }

    void RefreshIntegration() =>
        _integrationStatus.Text = BrowserIntegration.IsInstalled()
            ? "Native messaging host is registered."
            : "Native messaging host is not registered.";

    TabPage ConnectionTab()
    {
        var page = new TabPage("Connection");
        _connections.Value = _s.Connections;
        _concurrent.Value = _s.MaxConcurrent;
        _retries.Value = _s.MaxRetries;
        _limitOn.Checked = _s.SpeedLimitEnabled;
        _limit.Value = Math.Max(1, _s.SpeedLimitKBps == 0 ? 500 : _s.SpeedLimitKBps);
        page.Controls.AddRange([Lbl("Connections per download", 16, 16), _connections,
            Lbl("Downloads at the same time (queue)", 16, 50), _concurrent,
            Lbl("Retries for each connection", 16, 84), _retries,
            _limitOn, _limit, Lbl("KB/sec", 336, 120),
            new Label { Text = "More connections help on fast links, but some servers limit connections per user.", Location = new Point(16, 160), Size = new Size(500, 40), ForeColor = Color.DimGray }]);
        return page;
    }

    TabPage SaveToTab()
    {
        var page = new TabPage("Save to");
        _root.Text = _s.DownloadRoot;
        var browse = new Button { Text = "Browse...", Location = new Point(442, 35), Size = new Size(74, 25) };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _root.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) _root.Text = dlg.SelectedPath;
        };
        _categories.Columns.Add("Name", "Category");
        _categories.Columns.Add("Extensions", "File types (space separated)");
        _categories.Columns.Add("Folder", "Folder");
        _categories.Columns[0].FillWeight = 20;
        _categories.Columns[1].FillWeight = 45;
        _categories.Columns[2].FillWeight = 35;
        foreach (var c in _s.Categories) _categories.Rows.Add(c.Name, c.Extensions, c.Folder);
        page.Controls.AddRange([Lbl("Default download folder (General category)", 16, 14), _root, browse,
            Lbl("Categories: new downloads are filed by extension", 16, 78), _categories]);
        return page;
    }

    TabPage FileTypesTab()
    {
        var page = new TabPage("File types");
        _extensions.Text = _s.AutoExtensions;
        _userAgent.Text = _s.UserAgent;
        page.Controls.AddRange([Lbl("Take over these file types from the browser and clipboard (* wildcard allowed):", 16, 14), _extensions,
            Lbl("User-Agent for downloads added manually:", 16, 174), _userAgent]);
        return page;
    }

    void Collect()
    {
        _s.MonitorClipboard = _clipboard.Checked;
        _s.ShowStartDialog = _startDialog.Checked;
        _s.ShowProgressDialog = _progressDialog.Checked;
        _s.ShowCompleteDialog = _completeDialog.Checked;
        _s.Connections = (int)_connections.Value;
        _s.MaxConcurrent = (int)_concurrent.Value;
        _s.MaxRetries = (int)_retries.Value;
        _s.SpeedLimitEnabled = _limitOn.Checked;
        _s.SpeedLimitKBps = (int)_limit.Value;
        var oldRoot = _s.DownloadRoot;
        _s.DownloadRoot = _root.Text.Trim();
        _s.AutoExtensions = string.Join(' ', _extensions.Text.Split((char[])[' ', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries));
        _s.UserAgent = _userAgent.Text.Trim();
        _s.Categories = _categories.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow)
            .Select(r => (Name: (r.Cells[0].Value as string ?? "").Trim(), Ext: (r.Cells[1].Value as string ?? "").Trim(), Folder: (r.Cells[2].Value as string ?? "").Trim()))
            .Where(r => r.Name.Length > 0)
            .Select(r => new Category { Name = r.Name, Extensions = r.Ext, Folder = RebaseFolder(r.Folder, oldRoot, _s.DownloadRoot, r.Name) })
            .ToList();
    }

    /// <summary>Category folders under the old root follow the root when it changes.</summary>
    static string RebaseFolder(string folder, string oldRoot, string newRoot, string name)
    {
        if (folder.Length == 0) return name == Category.GeneralName ? newRoot : Path.Combine(newRoot, name);
        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase)) return folder;
        if (string.Equals(folder, oldRoot, StringComparison.OrdinalIgnoreCase)) return newRoot;
        var prefix = oldRoot.TrimEnd('\\') + "\\";
        return folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? Path.Combine(newRoot, folder[prefix.Length..]) : folder;
    }
}
