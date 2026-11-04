using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DailyPlan.App.Services;
using DailyPlan.App.ViewModels;
using DailyPlan.Client;
using TaskStatus = DailyPlan.Client.TaskStatus;

namespace DailyPlan.App;

public partial class MainWindow : Window
{
    private readonly CoreClient _core;
    private readonly MainViewModel _viewModel;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly SpeechService _speech;
    private readonly DispatcherTimer _reminderTimer;

    private bool _suppressVoiceToggle;
    private bool _syncingCalendar;
    private DateTime _lastReminderErrorAt = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        _settingsStore = new SettingsStore(AppPaths.SettingsFile);
        _settings = _settingsStore.Load();
        _core = new CoreClient(AppPaths.DatabaseFile);
        _viewModel = new MainViewModel(_core);
        DataContext = _viewModel;
        _speech = new SpeechService();

        _reminderTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _reminderTimer.Tick += ReminderTimer_Tick;

        ConfigureVoice();
        UpdateVoiceIndicators();

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.LoadTasks();
        }
        catch (Exception exception)
        {
            ShowToast($"加载任务失败：{exception.Message}");
        }

        _reminderTimer.Start();
        PollReminders();

        if (_settings.VoiceEnabled && !_speech.IsAvailable)
        {
            ShowToast("未检测到可用的语音引擎，语音提醒暂不可用。");
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _reminderTimer.Stop();
        _speech.Dispose();
        _core.Dispose();
    }

    private void ReminderTimer_Tick(object? sender, EventArgs e) => PollReminders();

    private void PollReminders()
    {
        var now = DateTime.Now;
        _viewModel.RefreshClock(now);

        try
        {
            var reminders = _viewModel.PollReminders(now);
            if (reminders.Count == 0)
            {
                return;
            }

            var messages = reminders.Select(BuildReminderMessage).ToList();
            ShowToast(string.Join(Environment.NewLine, messages));

            if (_settings.VoiceEnabled && _speech.IsAvailable)
            {
                foreach (var message in messages)
                {
                    _speech.TrySpeak(message);
                }
            }
        }
        catch (Exception exception)
        {
            if (now - _lastReminderErrorAt < TimeSpan.FromMinutes(1))
            {
                return;
            }

            _lastReminderErrorAt = now;
            ShowToast($"提醒检查失败：{exception.Message}");
        }
    }

    private static string BuildReminderMessage(ReminderEvent reminder)
    {
        var time = FriendlyTime(reminder.DueAt);
        return reminder.Kind == ReminderKind.Start
            ? $"{time}，任务「{reminder.Title}」开始。"
            : $"{time}，任务「{reminder.Title}」已到结束时间。";
    }

    private static string FriendlyTime(DateTime time)
    {
        var hour = time.Hour;
        var prefix = hour switch
        {
            < 6 => "凌晨",
            < 9 => "早上",
            < 12 => "上午",
            < 13 => "中午",
            < 18 => "下午",
            _ => "晚上"
        };

        var displayHour = hour switch
        {
            0 => 12,
            <= 12 => hour,
            _ => hour - 12
        };

        return time.Minute == 0
            ? $"{prefix}{displayHour}点"
            : $"{prefix}{displayHour}点{time.Minute}分";
    }

    private void ShowToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ToastText.Text = message;

        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(
            1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(
            1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.5))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(
            0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.9))));
        Toast.BeginAnimation(OpacityProperty, animation);
    }

    private void ChangeDate(DateTime date)
    {
        try
        {
            _viewModel.SelectedDate = date;
            _viewModel.LoadTasks();
            CalendarPopup.IsOpen = false;
        }
        catch (Exception exception)
        {
            ShowToast($"切换日期失败：{exception.Message}");
        }
    }

    private void PreviousDayButton_Click(object sender, RoutedEventArgs e) =>
        ChangeDate(_viewModel.SelectedDate.AddDays(-1));

    private void NextDayButton_Click(object sender, RoutedEventArgs e) =>
        ChangeDate(_viewModel.SelectedDate.AddDays(1));

    private void TodayButton_Click(object sender, RoutedEventArgs e) =>
        ChangeDate(DateTime.Today);

    private void DateButton_Click(object sender, RoutedEventArgs e)
    {
        if (CalendarPopup.IsOpen)
        {
            CalendarPopup.IsOpen = false;
            return;
        }

        _syncingCalendar = true;
        PlanCalendar.DisplayDate = _viewModel.SelectedDate;
        PlanCalendar.SelectedDate = _viewModel.SelectedDate;
        _syncingCalendar = false;
        CalendarPopup.IsOpen = true;
    }

    private void PlanCalendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCalendar || PlanCalendar.SelectedDate is not DateTime selected)
        {
            return;
        }

        ChangeDate(selected);
    }

    private void AddTaskButton_Click(object sender, RoutedEventArgs e)
    {
        var editor = new TaskEditorWindow(null, _viewModel.SelectedDate) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } draft)
        {
            return;
        }

        try
        {
            var task = _viewModel.CreateTask(draft);
            NavigateToTaskDate(task);
            ShowToast(task.IsRecurring
                ? $"已创建每天重复的任务「{task.Title}」"
                : $"已创建任务「{task.Title}」");
        }
        catch (Exception exception)
        {
            ShowToast(exception.Message);
        }
    }

    private void EditTask_Click(object sender, RoutedEventArgs e)
    {
        if (GetTask(sender) is not { } task)
        {
            return;
        }

        var editor = new TaskEditorWindow(task, _viewModel.SelectedDate) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } draft)
        {
            return;
        }

        try
        {
            var updated = _viewModel.UpdateTask(task.Id, draft);
            NavigateToTaskDate(updated);
            ShowToast("任务已更新");
        }
        catch (Exception exception)
        {
            ShowToast(exception.Message);
        }
    }

    private void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (GetTask(sender) is not { } task)
        {
            return;
        }

        var message = task.IsRecurring
            ? $"确定删除每天重复的任务「{task.Title}」吗？\n所有日期的这个计划都会被删除，此操作无法撤销。"
            : $"确定删除任务「{task.Title}」吗？\n此操作无法撤销。";
        var answer = MessageBox.Show(
            this,
            message,
            task.IsRecurring ? "删除重复任务" : "删除任务",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _viewModel.DeleteTask(task.Id);
            ShowToast(task.IsRecurring
                ? $"已删除每天重复的任务「{task.Title}」"
                : $"已删除任务「{task.Title}」");
        }
        catch (Exception exception)
        {
            ShowToast(exception.Message);
        }
    }

    private void StartTask_Click(object sender, RoutedEventArgs e) =>
        ChangeTaskStatus(GetTask(sender), TaskStatus.InProgress, "任务已开始");

    private void CompleteTask_Click(object sender, RoutedEventArgs e) =>
        ChangeTaskStatus(GetTask(sender), TaskStatus.Completed, "任务已完成");

    private void RestoreTask_Click(object sender, RoutedEventArgs e) =>
        ChangeTaskStatus(GetTask(sender), TaskStatus.Pending, "任务已恢复为待办");

    private void ChangeTaskStatus(TaskItem? task, TaskStatus status, string successMessage)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            _viewModel.SetStatus(task.Id, status, task.OccurrenceDate);
            ShowToast(task.IsRecurring
                ? $"{successMessage}（{task.OccurrenceDate:MM月dd日}）"
                : successMessage);
        }
        catch (Exception exception)
        {
            ShowToast(exception.Message);
        }
    }

    private void NavigateToTaskDate(TaskItem task)
    {
        // A repeating task's start date is the first occurrence, not necessarily
        // the day being edited. Stay on the selected day unless the series now
        // starts later than it.
        if (task.IsRecurring)
        {
            if (_viewModel.SelectedDate.Date < task.StartTime.Date)
            {
                ChangeDate(task.StartTime.Date);
            }
            return;
        }

        if (task.StartTime.Date != _viewModel.SelectedDate.Date)
        {
            ChangeDate(task.StartTime.Date);
        }
    }

    private static TaskItem? GetTask(object sender) =>
        (sender as FrameworkElement)?.DataContext as TaskItem;

    private void VoiceToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressVoiceToggle)
        {
            return;
        }

        var enabled = VoiceToggle.IsChecked == true;
        if (enabled && !_speech.IsAvailable)
        {
            _suppressVoiceToggle = true;
            VoiceToggle.IsChecked = false;
            _suppressVoiceToggle = false;
            ShowToast("未检测到可用的语音引擎，请先在 Windows 设置中安装语音包。");
            return;
        }

        _settings.VoiceEnabled = enabled;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception exception)
        {
            ShowToast($"设置保存失败：{exception.Message}");
        }

        UpdateVoiceIndicators();

        if (enabled)
        {
            ShowToast("语音提醒已开启");
            _speech.TrySpeak("语音提醒已开启");
        }
        else
        {
            ShowToast("语音提醒已关闭");
        }
    }

    private void ConfigureVoice()
    {
        _suppressVoiceToggle = true;
        VoiceToggle.IsChecked = _settings.VoiceEnabled && _speech.IsAvailable;
        _suppressVoiceToggle = false;

        if (!_speech.IsAvailable && _settings.VoiceEnabled)
        {
            _settings.VoiceEnabled = false;
            try
            {
                _settingsStore.Save(_settings);
            }
            catch
            {
                // Not being able to persist this preference is harmless.
            }
        }
    }

    private void UpdateVoiceIndicators()
    {
        var active = _settings.VoiceEnabled && _speech.IsAvailable;
        VoiceStatusDot.Fill = (Brush)FindResource(active ? "SuccessBrush" : "TextMutedBrush");
        SpeechStatusText.Text = _speech.IsAvailable
            ? $"语音引擎：{_speech.Description}"
            : "语音提醒不可用：未检测到可用的 Windows 语音";
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.DataDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ShowToast($"无法打开数据目录：{exception.Message}");
        }
    }
}
