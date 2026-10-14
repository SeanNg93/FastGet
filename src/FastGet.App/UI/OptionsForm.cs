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
    // "Save to" tab: one category at a time, like the IDM options page.
    readonly List<Category> _cats = new();
    readonly ComboBox _catCombo = new() { Location = new Point(12, 46), Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label _typesLabel = new() { Location = new Point(12, 86), Size = new Size(496, 20) };
    readonly TextBox _catTypes = new() { Location = new Point(12, 108), Size = new Size(496, 46), Multiline = true, ScrollBars = ScrollBars.Vertical };
    readonly Label _folderLabel = new() { Location = new Point(12, 166), Size = new Size(496, 20) };
    readonly TextBox _catFolder = new() { Location = new Point(12, 188), Width = 412 };
    readonly CheckBox _catRemember = new() { Location = new Point(12, 222), AutoSize = true };
    Category? _current;
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
        _cats.AddRange(_s.Categories.Select(c => c.Clone()));
        var group = new GroupBox { Text = "Categories, file types, folders", Location = new Point(8, 8), Size = new Size(520, 262) };
        var newBtn = new Button { Text = "New...", Location = new Point(320, 45), Size = new Size(90, 26) };
        var editBtn = new Button { Text = "Rename...", Location = new Point(416, 45), Size = new Size(92, 26) };
        var deleteBtn = new Button { Text = "Delete", Location = new Point(416, 77), Size = new Size(92, 26) };
        var browse = new Button { Text = "Browse...", Location = new Point(430, 187), Size = new Size(78, 25) };
        group.Controls.AddRange([new Label { Text = "Category", Location = new Point(12, 26), AutoSize = true }, _catCombo, newBtn, editBtn, deleteBtn,
            _typesLabel, _catTypes, _folderLabel, _catFolder, browse, _catRemember]);
        page.Controls.Add(group);
        page.Controls.Add(new Label
        {
            Text = "Downloads are filed by extension. Changing the General folder also moves the other category folders that are inside it.",
            Location = new Point(12, 278), Size = new Size(510, 40), ForeColor = Color.DimGray,
        });

        _catCombo.SelectedIndexChanged += (_, _) => ShowCategory(_cats.FirstOrDefault(c => c.Name == (string?)_catCombo.SelectedItem));
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _catFolder.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) _catFolder.Text = dlg.SelectedPath;
        };
        newBtn.Click += (_, _) =>
        {
            CommitCategory();
            var root = _cats.First(c => c.Name == Category.GeneralName).Folder;
            using var dlg = new CategoryForm(null, _cats, root);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _cats.Add(dlg.Result);
            _current = null;
            FillCategoryCombo(dlg.Result.Name);
        };
        editBtn.Click += (_, _) =>
        {
            if (_current == null) return;
            CommitCategory();
            using var dlg = new CategoryForm(_current, _cats, _s.DownloadRoot);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _cats[_cats.IndexOf(_current)] = dlg.Result;
            _current = null;
            FillCategoryCombo(dlg.Result.Name);
        };
        deleteBtn.Click += (_, _) =>
        {
            if (_current == null || _current.Name == Category.GeneralName) return;
            if (MessageBox.Show(this, $"Delete the \"{_current.Name}\" category? Files already downloaded are not touched.", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _cats.Remove(_current);
            _current = null;
            FillCategoryCombo(Category.GeneralName);
        };
        _catCombo.SelectedIndexChanged += (_, _) => deleteBtn.Enabled = (string?)_catCombo.SelectedItem != Category.GeneralName;
        FillCategoryCombo(Category.GeneralName);
        return page;
    }

    void FillCategoryCombo(string select)
    {
        _catCombo.Items.Clear();
        foreach (var c in _cats) _catCombo.Items.Add(c.Name);
        _catCombo.SelectedItem = select;
    }

    void ShowCategory(Category? c)
    {
        CommitCategory();
        _current = c;
        if (c == null) return;
        bool general = c.Name == Category.GeneralName;
        _typesLabel.Text = $"Automatically put in \"{c.Name}\" category the following file types:";
        _catTypes.ReadOnly = general;
        _catTypes.Text = general ? "The file types that are not listed in any other category" : c.Extensions;
        _folderLabel.Text = $"Default download directory for \"{c.Name}\" category";
        _catFolder.Text = c.Folder;
        _catRemember.Text = $"Change folder for \"{c.Name}\" category on last selected";
        _catRemember.Checked = c.RememberLastPath;
    }

    /// <summary>Writes the visible fields back into the category being shown.</summary>
    void CommitCategory()
    {
        if (_current == null) return;
        if (_current.Name != Category.GeneralName)
            _current.Extensions = string.Join(' ', _catTypes.Text.Split((char[])[' ', '\r', '\n', ',', ';', '.'], StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (Path.IsPathRooted(_catFolder.Text.Trim())) _current.Folder = _catFolder.Text.Trim();
        _current.RememberLastPath = _catRemember.Checked;
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
        CommitCategory();
        var oldRoot = _s.DownloadRoot;
        var general = _cats.First(c => c.Name == Category.GeneralName);
        _s.DownloadRoot = general.Folder;
        _s.AutoExtensions = string.Join(' ', _extensions.Text.Split((char[])[' ', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries));
        _s.UserAgent = _userAgent.Text.Trim();
        foreach (var c in _cats.Where(c => c != general))
            c.Folder = RebaseFolder(c.Folder, oldRoot, _s.DownloadRoot, c.Name);
        _s.Categories = _cats;
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
