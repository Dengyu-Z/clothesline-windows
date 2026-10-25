using Forms = System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace SnapshotRail;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private System.Threading.Mutex? _instanceMutex;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        // Concurrent tray instances would overwrite each other's rail state.
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _instanceMutex = new System.Threading.Mutex(true, @"Local\Clothesline-" + user, out var firstInstance);
        if (!firstInstance)
        {
            Shutdown();
            return;
        }
        _window = new MainWindow();
        MainWindow = _window;
        _window.Show();

        _tray = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "Clothesline",
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        var show = new Forms.ToolStripMenuItem("显示 / 隐藏横条");
        menu.Items.Add(show);
        show.Click += (_, _) => _window.ToggleRail();
        var clipboard = new Forms.ToolStripMenuItem("自动挂载复制的图片");
        menu.Items.Add(clipboard);
        clipboard.CheckOnClick = true;
        // First launch defaults to on; later launches restore the user's last
        // choice from the current Windows user profile.
        clipboard.Checked = _window.ClipboardMonitoring;
        clipboard.Click += (_, _) => _window.ClipboardMonitoring = clipboard.Checked;
        var autoHide = new Forms.ToolStripMenuItem("鼠标离开后自动隐藏");
        menu.Items.Add(autoHide);
        autoHide.CheckOnClick = true;
        autoHide.Checked = true;
        autoHide.Click += (_, _) => _window.AutoHideEnabled = autoHide.Checked;
        var size = new Forms.ToolStripMenuItem("卡片大小");
        size.DropDownItems.Add(ScaleItem("小", 0.8, _window));
        size.DropDownItems.Add(ScaleItem("中", 1.0, _window));
        size.DropDownItems.Add(ScaleItem("大", 1.25, _window));
        menu.Items.Add(size);
        var capacity = new Forms.ToolStripMenuItem("绳上最多保留");
        capacity.DropDownItems.Add(CapacityItem("12 张", 12, _window));
        capacity.DropDownItems.Add(CapacityItem("20 张", 20, _window));
        capacity.DropDownItems.Add(CapacityItem("30 张", 30, _window));
        menu.Items.Add(capacity);
        var addFolder = new Forms.ToolStripMenuItem("添加监视文件夹…");
        addFolder.Click += (_, _) =>
        {
            using var dialog = new Forms.FolderBrowserDialog { Description = "选择要自动挂到晾衣绳的图片文件夹" };
            if (dialog.ShowDialog() == Forms.DialogResult.OK) _window.AddWatchedFolder(dialog.SelectedPath);
        };
        menu.Items.Add(addFolder);
        var removeFolder = new Forms.ToolStripMenuItem("停止监视文件夹…");
        removeFolder.Click += (_, _) =>
        {
            using var dialog = new Forms.FolderBrowserDialog { Description = "选择要停止监视的文件夹" };
            if (dialog.ShowDialog() == Forms.DialogResult.OK) _window.RemoveWatchedFolder(dialog.SelectedPath);
        };
        menu.Items.Add(removeFolder);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var exit = new Forms.ToolStripMenuItem("退出 Clothesline");
        menu.Items.Add(exit);
        exit.Click += (_, _) => Shutdown();
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => _window.ToggleRail();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _tray?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static Forms.ToolStripMenuItem ScaleItem(string text, double scale, MainWindow window)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => window.SetCardScale(scale);
        return item;
    }

    private static Forms.ToolStripMenuItem CapacityItem(string text, int count, MainWindow window)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => window.MaximumItems = count;
        return item;
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var background = new LinearGradientBrush(new Rectangle(0, 0, 64, 64),
                   Color.FromArgb(255, 40, 76, 120), Color.FromArgb(255, 214, 133, 184), 35f))
            graphics.FillRoundedRectangle(background, new Rectangle(3, 3, 58, 58), 14);

        using var rope = new Pen(Color.FromArgb(235, 245, 248, 255), 2.2f);
        graphics.DrawArc(rope, 0, 10, 64, 30, 8, 164);
        using var frame = new SolidBrush(Color.FromArgb(235, 250, 252, 255));
        using var photo = new LinearGradientBrush(new Rectangle(31, 24, 23, 23),
            Color.FromArgb(255, 104, 159, 230), Color.FromArgb(255, 107, 69, 142), 90f);
        graphics.FillRoundedRectangle(frame, new Rectangle(9, 23, 25, 27), 5);
        graphics.FillRoundedRectangle(photo, new Rectangle(34, 24, 22, 23), 5);
        using var sun = new SolidBrush(Color.FromArgb(255, 255, 210, 108));
        graphics.FillEllipse(sun, 43, 27, 7, 7);
        using var line = new Pen(Color.FromArgb(255, 91, 116, 172), 2.3f);
        graphics.DrawLine(line, 13, 32, 29, 32);
        graphics.DrawLine(line, 13, 38, 29, 38);
        graphics.DrawLine(line, 13, 44, 25, 44);
        using var clip = new Pen(Color.FromArgb(255, 215, 227, 240), 3f);
        graphics.DrawLine(clip, 20, 13, 20, 24);
        graphics.DrawLine(clip, 47, 14, 47, 24);
        var handle = bitmap.GetHicon();
        using var icon = Icon.FromHandle(handle);
        return (Icon)icon.Clone();
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
