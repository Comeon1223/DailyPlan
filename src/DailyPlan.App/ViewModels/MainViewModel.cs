using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DailyPlan.Client;
using TaskStatus = DailyPlan.Client.TaskStatus;

namespace DailyPlan.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan ReminderGrace = TimeSpan.FromMinutes(2);

    private readonly CoreClient _core;
    private DateTime _selectedDate = DateTime.Today;

    public MainViewModel(CoreClient core)
    {
        _core = core;
    }

    public ObservableCollection<TaskItem> Tasks { get; } = [];

    public DateTime SelectedDate
    {
        get => _selectedDate;
        set
        {
            var normalized = value.Date;
            if (_selectedDate == normalized)
            {
                return;
            }

            _selectedDate = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DateTitle));
            OnPropertyChanged(nameof(WeekdayText));
            OnPropertyChanged(nameof(RelativeDayText));
            OnPropertyChanged(nameof(HasRelativeDay));
            OnPropertyChanged(nameof(IsToday));
        }
    }

    public string DateTitle => $"{SelectedDate:yyyy年M月d日}";

    public string WeekdayText => "星期" + "日一二三四五六"[(int)SelectedDate.DayOfWeek];

    public string RelativeDayText
    {
        get
        {
            var delta = (SelectedDate.Date - DateTime.Today).Days;
            return delta switch
            {
                0 => "今天",
                1 => "明天",
                -1 => "昨天",
                _ => ""
            };
        }
    }

    public bool HasRelativeDay => !string.IsNullOrEmpty(RelativeDayText);

    public bool IsToday => SelectedDate.Date == DateTime.Today;

    public int TotalCount => Tasks.Count;
    public int ActiveCount => Tasks.Count(task => task.IsActive);
    public int CompletedCount => Tasks.Count(task => task.IsCompleted);
    public bool IsEmpty => Tasks.Count == 0;

    public double ProgressPercent =>
        TotalCount == 0 ? 0 : Math.Round(CompletedCount * 100.0 / TotalCount, MidpointRounding.AwayFromZero);

    public string ProgressPercentText => $"{ProgressPercent:0}%";
    public string TotalCountText => $"{TotalCount} 项";
    public string ActiveCountText => $"{ActiveCount} 项";
    public string CompletedCountText => $"{CompletedCount} 项";

    public void LoadTasks()
    {
        var tasks = _core.ListTasks(SelectedDate);
        var now = DateTime.Now;

        Tasks.Clear();
        foreach (var task in tasks)
        {
            task.RefreshClock(now);
            Tasks.Add(task);
        }

        NotifySummaryChanged();
    }

    public TaskItem CreateTask(TaskDraft draft)
    {
        var task = _core.CreateTask(draft);
        LoadTasks();
        return task;
    }

    public TaskItem UpdateTask(long id, TaskDraft draft)
    {
        var task = _core.UpdateTask(id, draft);
        LoadTasks();
        return task;
    }

    public void DeleteTask(long id)
    {
        _core.DeleteTask(id);
        LoadTasks();
    }

    public TaskItem SetStatus(long id, TaskStatus status, DateTime occurrenceDate)
    {
        var task = _core.SetStatus(id, status, occurrenceDate);
        LoadTasks();
        return task;
    }

    public IReadOnlyList<ReminderEvent> PollReminders(DateTime now) =>
        _core.PollReminders(now, ReminderGrace);

    public void RefreshClock(DateTime now)
    {
        foreach (var task in Tasks)
        {
            task.RefreshClock(now);
        }
    }

    public void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressPercentText));
        OnPropertyChanged(nameof(TotalCountText));
        OnPropertyChanged(nameof(ActiveCountText));
        OnPropertyChanged(nameof(CompletedCountText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
