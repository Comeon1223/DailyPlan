using System.IO;
using System.Windows;
using System.Windows.Threading;
using DailyPlan.App.Services;

namespace DailyPlan.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            AppPaths.EnsureDataDirectory();
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            TryLog(exception);
            MessageBox.Show(
                $"应用启动失败：\n{exception.Message}",
                "每天计划",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog(e.Exception);
        MessageBox.Show(
            $"发生了一个未处理的错误：\n{e.Exception.Message}\n\n详细信息已写入日志。",
            "每天计划",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void TryLog(Exception exception)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            File.AppendAllText(
                AppPaths.ErrorLogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never bring the app down.
        }
    }
}
