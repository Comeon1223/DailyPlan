using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DailyPlan.Client;

public enum TaskStatus
{
    Pending,
    InProgress,
    Completed,
    Cancelled
}

public enum ReminderKind
{
    Start,
    End
}

public static class TaskStatusExtensions
{
    public static string ToApiValue(this TaskStatus status) => status switch
    {
        TaskStatus.Pending => "pending",
        TaskStatus.InProgress => "in_progress",
        TaskStatus.Completed => "completed",
        TaskStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static string ToLabel(this TaskStatus status) => status switch
    {
        TaskStatus.Pending => "待办",
        TaskStatus.InProgress => "进行中",
        TaskStatus.Completed => "已完成",
        TaskStatus.Cancelled => "已取消",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static TaskStatus ParseApiValue(string value) => value switch
    {
        "pending" => TaskStatus.Pending,
        "in_progress" => TaskStatus.InProgress,
        "completed" => TaskStatus.Completed,
        "cancelled" => TaskStatus.Cancelled,
        _ => throw new FormatException($"未知的任务状态：{value}")
    };

    public static bool IsActive(this TaskStatus status) =>
        status is TaskStatus.Pending or TaskStatus.InProgress;

    public static bool IsFinished(this TaskStatus status) =>
        status is TaskStatus.Completed or TaskStatus.Cancelled;
}

public sealed record TaskStatusOption(TaskStatus Value, string Label);

public static class TaskStatusCatalog
{
    public static IReadOnlyList<TaskStatusOption> All { get; } =
    [
        new(TaskStatus.Pending, "待办"),
        new(TaskStatus.InProgress, "进行中"),
        new(TaskStatus.Completed, "已完成"),
        new(TaskStatus.Cancelled, "已取消")
    ];
}

public sealed record TaskDraft(
    string Title,
    string Notes,
    DateTime StartTime,
    DateTime EndTime,
    TaskStatus Status,
    bool RepeatDaily = false);

public sealed record ReminderEvent(
    long TaskId,
    string Title,
    ReminderKind Kind,
    DateTime DueAt,
    TaskStatus Status);

public sealed class TaskItem : INotifyPropertyChanged
{
    private DateTime _now;

    public TaskItem(
        long id,
        string title,
        string notes,
        DateTime startTime,
        DateTime endTime,
        TaskStatus status,
        bool startNotified,
        bool endNotified,
        DateTime createdAt,
        DateTime updatedAt,
        bool repeatDaily = false,
        DateTime? now = null)
    {
        Id = id;
        Title = title;
        Notes = notes;
        StartTime = startTime;
        EndTime = endTime;
        Status = status;
        StartNotified = startNotified;
        EndNotified = endNotified;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        RepeatDaily = repeatDaily;
        _now = now ?? DateTime.Now;
    }

    public long Id { get; }
    public string Title { get; }
    public string Notes { get; }
    public DateTime StartTime { get; }
    public DateTime EndTime { get; }
    public TaskStatus Status { get; }
    public bool StartNotified { get; }
    public bool EndNotified { get; }
    public DateTime CreatedAt { get; }
    public DateTime UpdatedAt { get; }
    public bool RepeatDaily { get; }

    public bool IsRecurring => RepeatDaily;

    /// <summary>The day this item represents. Repeating tasks are expanded per day.</summary>
    public DateTime OccurrenceDate => StartTime.Date;

    public string StartTimeText => StartTime.ToString("HH:mm");
    public string EndTimeText => EndTime.ToString("HH:mm");
    public string StatusText => Status.ToLabel();

    public string TimeRangeText
    {
        get
        {
            if (StartTime.Date == EndTime.Date)
            {
                return $"{StartTime:HH:mm} – {EndTime:HH:mm}";
            }

            if (EndTime.Date == StartTime.Date.AddDays(1))
            {
                return $"{StartTime:HH:mm} – 次日 {EndTime:HH:mm}";
            }

            return $"{StartTime:MM-dd HH:mm} – {EndTime:MM-dd HH:mm}";
        }
    }

    public string DurationText
    {
        get
        {
            var totalMinutes = (int)Math.Round((EndTime - StartTime).TotalMinutes);
            if (totalMinutes < 1)
            {
                return "不足 1 分钟";
            }

            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;
            if (hours == 0)
            {
                return $"{minutes} 分钟";
            }

            return minutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {minutes} 分钟";
        }
    }

    public bool IsCompleted => Status == TaskStatus.Completed;
    public bool IsCancelled => Status == TaskStatus.Cancelled;
    public bool IsActive => Status.IsActive();
    public bool CanStart => Status == TaskStatus.Pending;
    public bool CanComplete => Status is TaskStatus.Pending or TaskStatus.InProgress;
    public bool CanRestore => Status.IsFinished();
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>Time has passed the end while the task is still active.</summary>
    public bool IsOverdue => IsActive && EndTime < _now;

    /// <summary>The current clock is between start and end.</summary>
    public bool IsWithinWindow => IsActive && _now >= StartTime && _now <= EndTime;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshClock(DateTime now)
    {
        if (_now.Date == now.Date && _now.Hour == now.Hour && _now.Minute == now.Minute)
        {
            return;
        }

        _now = now;
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(IsWithinWindow));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
