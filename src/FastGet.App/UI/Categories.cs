using FastGet.Core;

namespace FastGet.App.UI;

/// <summary>Create or edit a category: name, file types and the folder its downloads go to.</summary>
sealed class CategoryForm : DialogBase
{
    readonly TextBox _name = new() { Location = new Point(14, 36), Width = 480 };
    readonly TextBox _types = new() { Location = new Point(14, 100), Size = new Size(480, 50), Multiline = true, ScrollBars = ScrollBars.Vertical };
    readonly TextBox _folder = new() { Location = new Point(14, 196), Width = 400 };
    readonly Button _browse = new() { Text = "Browse...", Location = new Point(420, 195), Size = new Size(74, 25) };
    readonly CheckBox _remember = new() { Text = "Remember the last folder chosen for this category", Location = new Point(14, 228), AutoSize = true };
    readonly IReadOnlyCollection<string> _otherNames;
    readonly bool _isGeneral;

    public CategoryForm(Category? existing, IEnumerable<Category> all, string defaultRoot) : base(existing == null ? "New category" : "Edit category", 508, 300)
    {
        _isGeneral = existing?.Name == Category.GeneralName;
        _otherNames = all.Where(c => c.Name != existing?.Name).Select(c => c.Name).ToList();
        Controls.AddRange([
            new Label { Text = "Category name", Location = new Point(14, 14), AutoSize = true }, _name,
            new Label { Text = "Automatically put in this category the following file types:", Location = new Point(14, 70), AutoSize = true }, _types,
            new Label { Text = "Separate extensions with spaces, e.g. avi mkv mp4", Location = new Point(14, 152), AutoSize = true, ForeColor = Color.DimGray },
            new Label { Text = "Save downloads of this category to:", Location = new Point(14, 176), AutoSize = true }, _folder, _browse, _remember,
        ]);
        var ok = Btn("OK", 280, 262, 104);
        var cancel = Btn("Cancel", 390, 262, 104);
        ok.Click += (_, _) => Accept();
        cancel.DialogResult = DialogResult.Cancel;
        Controls.AddRange([ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
        _browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _folder.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
        };

        Result = existing?.Clone() ?? new Category();
        _name.Text = Result.Name;
        _types.Text = Result.Extensions;
        _folder.Text = Result.Folder;
        _remember.Checked = Result.RememberLastPath;
        if (_isGeneral)
        {
            // General is the fallback for every type no other category claims, so it has no list and keeps its name.
            _name.ReadOnly = true;
            _types.Text = "The file types that are not listed in any other category";
            _types.ReadOnly = true;
        }
        // A new category gets a folder named after it, updated while the name is typed.
        if (existing == null)
            _name.TextChanged += (_, _) => _folder.Text = Path.Combine(defaultRoot, Http.Sanitize(_name.Text.Trim().Length == 0 ? "New" : _name.Text.Trim()));
    }

    public Category Result { get; }

    void Accept()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0 || _otherNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, name.Length == 0 ? "Enter a category name." : $"A category named \"{name}\" already exists.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!Path.IsPathRooted(_folder.Text.Trim()))
        {
            MessageBox.Show(this, "Choose a full folder path.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Result.Name = name;
        if (!_isGeneral)
            Result.Extensions = string.Join(' ', _types.Text.Split((char[])[' ', '\r', '\n', ',', ';', '.'], StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        Result.Folder = _folder.Text.Trim();
        Result.RememberLastPath = _remember.Checked;
        DialogResult = DialogResult.OK;
    }
}

/// <summary>
/// The "Category / Save As / Remember this path" block of the download dialogs. Picking a category moves the file into
/// that category's folder; "+" creates a category on the spot; the checkbox makes the chosen folder the category default.
/// Laid out in 96-DPI units, 556 wide and 92 high.
/// </summary>
sealed class SaveTargetPanel : Panel
{
    readonly DownloadManager _manager;
    readonly ComboBox _category = new() { Location = new Point(86, 0), Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Button _add = new() { Text = "+", Location = new Point(312, -1), Size = new Size(28, 25) };
    readonly TextBox _path = new() { Location = new Point(86, 34), Width = 430 };
    readonly Button _browse = new() { Text = "...", Location = new Point(522, 33), Size = new Size(34, 25) };
    readonly CheckBox _remember = new() { Location = new Point(86, 64), AutoSize = true };
    readonly ToolTip _tips = new();
    bool _settingPath;

    public SaveTargetPanel(DownloadManager manager)
    {
        _manager = manager;
        Size = new Size(556, 92);
        Controls.AddRange([
            new Label { Text = "Category", Location = new Point(0, 3), Size = new Size(84, 20) }, _category, _add,
            new Label { Text = "Save As", Location = new Point(0, 37), Size = new Size(84, 20) }, _path, _browse, _remember,
        ]);
        _tips.SetToolTip(_add, "New category");
        FillCategories(null);
        _category.SelectedIndexChanged += (_, _) => OnCategoryChanged();
        _path.TextChanged += (_, _) => { if (!_settingPath) UserEditedPath = true; };
        _add.Click += (_, _) => AddCategory();
        _browse.Click += (_, _) => Browse();
    }

    /// <summary>The user typed or browsed a path, so server-provided names must not overwrite it.</summary>
    public bool UserEditedPath { get; private set; }
    public string FullPath => _path.Text.Trim();
    public string? CategoryName => (string?)_category.SelectedItem;
    Category? SelectedCategory => _manager.Settings.Categories.FirstOrDefault(c => c.Name == CategoryName);

    void FillCategories(string? select)
    {
        _category.Items.Clear();
        foreach (var c in _manager.Settings.Categories) _category.Items.Add(c.Name);
        if (select != null) _category.SelectedItem = select;
    }

    void SetPath(string path)
    {
        _settingPath = true;
        _path.Text = path;
        _path.Select(0, 0); // keep the start of a long path visible
        _settingPath = false;
    }

    /// <summary>Picks the category from the extension (unless forced) and puts the file in its folder.</summary>
    public void SetFile(string fileName, string? forceCategory = null)
    {
        var category = _manager.Settings.Categories.FirstOrDefault(c => c.Name == forceCategory)
                       ?? _manager.Settings.ResolveCategory(fileName);
        _category.SelectedItem = category.Name; // triggers OnCategoryChanged with the old file name
        SetPath(Path.Combine(category.Folder, Http.Sanitize(fileName)));
    }

    /// <summary>Changes only the extension, e.g. when another video quality produces .mp4 instead of .ts.</summary>
    public void SetExtension(string extension)
    {
        if (FullPath.Length > 0) SetPath(Path.ChangeExtension(FullPath, extension));
    }

    void OnCategoryChanged()
    {
        var c = SelectedCategory;
        if (c == null) return;
        var name = Path.GetFileName(FullPath);
        if (name.Length > 0) SetPath(Path.Combine(c.Folder, name));
        _remember.Text = $"Remember this path for \"{c.Name}\" category";
        _remember.Checked = c.RememberLastPath;
    }

    void AddCategory()
    {
        using var dlg = new CategoryForm(null, _manager.Settings.Categories, _manager.Settings.DownloadRoot);
        var ext = Path.GetExtension(FullPath).TrimStart('.');
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        // Offer the current file's type to the new category if the user left the list empty.
        if (dlg.Result.Extensions.Length == 0 && ext.Length > 0) dlg.Result.Extensions = ext.ToLowerInvariant();
        _manager.SaveCategory(dlg.Result);
        FillCategories(dlg.Result.Name);
    }

    void Browse()
    {
        using var dlg = new SaveFileDialog
        {
            FileName = Path.GetFileName(FullPath),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(FullPath)) ? Path.GetDirectoryName(FullPath) : _manager.Settings.DownloadRoot,
            OverwritePrompt = false,
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        SetPath(dlg.FileName);
        UserEditedPath = true;
    }

    /// <summary>Validates the path and, when asked, stores the chosen folder as the category's default.</summary>
    public bool Commit(IWin32Window owner)
    {
        var path = FullPath;
        if (Path.GetFileName(path).Length == 0 || !Path.IsPathRooted(path))
        {
            MessageBox.Show(owner, "Please choose a full file path.", "FastGet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        var c = SelectedCategory;
        var folder = Path.GetDirectoryName(path)!;
        if (c != null && (_remember.Checked != c.RememberLastPath
                          || _remember.Checked && !string.Equals(folder.TrimEnd('\\'), c.Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
        {
            var updated = c.Clone();
            updated.RememberLastPath = _remember.Checked;
            if (_remember.Checked) updated.Folder = folder;
            _manager.SaveCategory(updated);
        }
        return true;
    }
}
