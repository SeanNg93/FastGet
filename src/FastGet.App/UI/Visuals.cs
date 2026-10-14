using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using FastGet.Core;

namespace FastGet.App.UI;

/// <summary>
/// Dialogs are laid out in 96-DPI pixels. Fonts are in points and already follow the monitor DPI,
/// so only control bounds need scaling, once, when the form is shown.
/// </summary>
static class Dpi
{
    public static void ScaleOnLoad(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.None;
        form.Load += (_, _) =>
        {
            float k = form.DeviceDpi / 96f;
            if (Math.Abs(k - 1f) < 0.01f) return;
            form.Scale(new SizeF(k, k));
            ScaleColumns(form, k);
            ResetTextBoxes(form);
            if (form.StartPosition == FormStartPosition.CenterParent && form.Owner != null) form.Location = Center(form, form.Owner.Bounds);
            else form.Location = Center(form, Screen.FromControl(form).WorkingArea);
        };
    }

    /// <summary>ListView column widths are not part of control bounds, so Scale() leaves them alone.</summary>
    static void ScaleColumns(Control parent, float k)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is ListView list)
                foreach (ColumnHeader col in list.Columns) col.Width = (int)(col.Width * k);
            ScaleColumns(c, k);
        }
    }

    /// <summary>
    /// A single-line edit control keeps the scroll offset and margins it had at its unscaled width, which clips the first
    /// characters after Scale(). Recompute margins from the font and scroll back to the start.
    /// </summary>
    static void ResetTextBoxes(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is TextBox { Multiline: false, IsHandleCreated: true } tb)
            {
                SendMessage(tb.Handle, EM_SETMARGINS, EC_LEFTMARGIN | EC_RIGHTMARGIN, EC_USEFONTINFO);
                var text = tb.Text;
                tb.Text = string.Empty;
                tb.Text = text;
                tb.Select(0, 0);
            }
            ResetTextBoxes(c);
        }
    }

    const int EM_SETMARGINS = 0xD3, EC_LEFTMARGIN = 1, EC_RIGHTMARGIN = 2, EC_USEFONTINFO = 0xFFFF;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    static Point Center(Form f, Rectangle area) =>
        new(area.Left + (area.Width - f.Width) / 2, Math.Max(area.Top, area.Top + (area.Height - f.Height) / 2));
}

static class Fmt
{
    public static string Size(long bytes) => bytes switch
    {
        < 0 => "",
        < 1024 => $"{bytes} Bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.##} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.##} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    public static string Speed(double bytesPerSecond) => bytesPerSecond <= 0 ? "" : Size((long)bytesPerSecond) + "/sec";

    public static string Time(TimeSpan? t)
    {
        if (t is not { } v) return "";
        if (v.TotalDays >= 1) return $"{(int)v.TotalDays} day(s) {v.Hours} hr";
        if (v.TotalHours >= 1) return $"{(int)v.TotalHours} hr {v.Minutes} min";
        if (v.TotalMinutes >= 1) return $"{v.Minutes} min {v.Seconds} sec";
        return $"{v.Seconds} sec";
    }

    public static string Status(DownloadItem i) => i.Status switch
    {
        DownloadStatus.Completed => "Complete",
        DownloadStatus.Connecting => "Connecting...",
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Error => "Error",
        DownloadStatus.Downloading => i.HasKnownProgress ? $"{i.Progress * 100:0.00}%" : Size(i.Downloaded),
        _ => i.Downloaded > 0 && i.HasKnownProgress ? $"{i.Progress * 100:0.00}%" : "",
    };
}

/// <summary>Toolbar and tree icons drawn from the Segoe MDL2 Assets font, so the app ships without image files.</summary>
static class Glyphs
{
    public const string Add = "", Play = "", Pause = "", Stop = "", Delete = "",
        Clear = "", Settings = "", Queue = "", Download = "", Folder = "",
        Video = "", Music = "", Document = "", Package = "", Apps = "",
        Check = "", Sync = "", All = "", Link = "", Batch = "", Clock = "";

    static readonly FontFamily? Family = FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe MDL2 Assets");

    public static Bitmap Render(string glyph, Color color, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = Family != null
            ? new Font(Family, size * 0.72f, GraphicsUnit.Pixel)
            : new Font(FontFamily.GenericSansSerif, size * 0.6f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Family != null ? glyph : "•", font, brush, new RectangleF(0, 1, size, size), format);
        return bmp;
    }

    /// <summary>App icon: white download arrow on a blue rounded square.</summary>
    public static Icon AppIcon()
    {
        const int size = 64;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            const int r = 14;
            path.AddArc(0, 0, r, r, 180, 90);
            path.AddArc(size - r - 1, 0, r, r, 270, 90);
            path.AddArc(size - r - 1, size - r - 1, r, r, 0, 90);
            path.AddArc(0, size - r - 1, r, r, 90, 90);
            using var bg = new LinearGradientBrush(new Rectangle(0, 0, size, size), Color.FromArgb(0, 132, 255), Color.FromArgb(0, 84, 180), 90f);
            g.FillPath(bg, path);
            using var arrow = Render(Download, Color.White, 52);
            g.DrawImage(arrow, 6, 6);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

/// <summary>Shell icons per file extension, as Explorer would show them.</summary>
sealed class FileIcons
{
    readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

    public ImageList List { get; } = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };

    public int For(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (_index.TryGetValue(ext, out var i)) return i;
        var info = new SHFILEINFO();
        var res = SHGetFileInfo(string.IsNullOrEmpty(ext) ? "file" : "file" + ext, 0x80, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return _index[ext] = -1;
        using (var icon = Icon.FromHandle(info.hIcon)) List.Images.Add(icon.ToBitmap());
        DestroyIcon(info.hIcon);
        return _index[ext] = List.Images.Count - 1;
    }

    const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>Shows where each connection started and how far it got, like the classic "download progress by connections" bar.</summary>
sealed class SegmentBar : Control
{
    List<Segment> _segments = new();
    long _total = -1;

    public SegmentBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 22;
    }

    public void SetSegments(List<Segment> segments, long total)
    {
        _segments = segments;
        _total = total;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        g.FillRectangle(Brushes.White, r);
        if (_total > 0)
        {
            using var done = new SolidBrush(Color.FromArgb(38, 120, 220));
            using var active = new SolidBrush(Color.FromArgb(20, 170, 70));
            foreach (var s in _segments)
            {
                float x0 = (float)((double)s.Start / _total * r.Width);
                float x1 = (float)((double)Math.Min(s.Position, _total) / _total * r.Width);
                if (x1 > x0) g.FillRectangle(done, x0 + 1, 1, x1 - x0, r.Height - 1);
                if (s.Active)
                    g.FillRectangle(active, Math.Max(x0, x1 - 3) + 1, 1, 3, r.Height - 1);
                g.DrawLine(Pens.DarkGray, x0, 0, x0, r.Height);
            }
        }
        g.DrawRectangle(Pens.Gray, r);
    }
}
