using System.IO;

namespace DailyPlan.App.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DailyPlan");

    public static string DatabaseFile { get; } = Path.Combine(DataDirectory, "dailyplan.db");

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");

    public static string ErrorLogFile { get; } = Path.Combine(DataDirectory, "error.log");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
}
