using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace DailyPlan.App.Services;

/// <summary>
/// 系统托盘常驻图标：左键单击显示 / 隐藏主窗口，双击显示，右键菜单提供显示、隐藏与退出。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Window _window;
    private readonly Action _onExitRequested;
    private bool _disposed;

    public TrayIconService(Window window, Action onExitRequested)
    {
        _window = window;
        _onExitRequested = onExitRequested;

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "每天计划",
            Icon = LoadAppIcon(),
            Visible = true
        };

        _notifyIcon.Click += (_, _) => ToggleWindow();
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => ShowWindow());
        menu.Items.Add("隐藏主界面", null, (_, _) => HideWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _onExitRequested());
        _notifyIcon.ContextMenuStrip = menu;
    }

    public void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    public void HideWindow() => _window.Hide();

    private void ToggleWindow()
    {
        if (_window.IsVisible)
        {
            HideWindow();
        }
        else
        {
            ShowWindow();
        }
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
            {
                return Icon.ExtractAssociatedIcon(processPath) ?? SystemIcons.Application;
            }
        }
        catch
        {
            // Fall through to the default icon.
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
