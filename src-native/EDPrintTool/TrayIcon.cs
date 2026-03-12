using Microsoft.Win32;

namespace EDPrintTool;

public class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Form _mainForm;
    private bool _isQuitting;
    private readonly ToolStripMenuItem _autoStartItem;

    private const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "EDPrintTool";

    public TrayIcon(Form mainForm)
    {
        _mainForm = mainForm;

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "EDPrintTool — ZPL Label Printer",
            Visible = true,
        };

        _autoStartItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled(),
        };
        _autoStartItem.CheckedChanged += (_, _) => SetAutoStart(_autoStartItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open EDPrintTool", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Open in Browser", null, (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "http://localhost:8189",
                UseShellExecute = true,
            });
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Server: localhost:8189") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit EDPrintTool", null, (_, _) =>
        {
            _isQuitting = true;
            Application.Exit();
        });

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowMainWindow();

        // Intercept form closing → minimize to tray
        _mainForm.FormClosing += (_, e) =>
        {
            if (!_isQuitting)
            {
                e.Cancel = true;
                _mainForm.Hide();
            }
        };
    }

    private void ShowMainWindow()
    {
        _mainForm.Show();
        _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.BringToFront();
        _mainForm.Activate();
    }

    private static Icon CreateIcon()
    {
        // Create a simple 16x16 blue printer icon
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        // Paper (top)
        g.FillRectangle(new SolidBrush(Color.FromArgb(230, 230, 230)), 4, 1, 8, 4);
        // Printer body
        g.FillRectangle(new SolidBrush(Color.FromArgb(79, 140, 255)), 2, 5, 12, 6);
        // Output slot
        g.FillRectangle(new SolidBrush(Color.FromArgb(50, 100, 220)), 4, 8, 8, 2);
        // Output paper
        g.FillRectangle(new SolidBrush(Color.FromArgb(240, 240, 240)), 4, 11, 8, 4);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch { return false; }
    }

    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, true);
            if (key == null) return;
            if (enable)
                key.SetValue(AppName, $"\"{Application.ExecutablePath}\"");
            else
                key.DeleteValue(AppName, false);
        }
        catch { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
