using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DailyPlan.Client;

public sealed class CoreException : Exception
{
    public CoreException(string message) : base(message) { }
    public CoreException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Managed wrapper around the native C ABI in DailyPlanCore.dll.
/// Every call is serialised with a lock so the UI timer and commands cannot
/// enter the native context at the same time.
/// </summary>
public sealed class CoreClient : IDisposable
{
    private const string NativeLibrary = "DailyPlanCore";
    private const string LocalDateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private IntPtr _context;
    private bool _disposed;

    public CoreClient(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DatabasePath = fullPath;

        try
        {
            _context = Native.dp_create(fullPath, out var errorPointer);
            var error = TakeError(errorPointer);
            if (_context == IntPtr.Zero)
            {
                throw new CoreException(error ?? "无法初始化本地数据库");
            }
        }
        catch (DllNotFoundException exception)
        {
            throw new CoreException(
                "找不到本地核心 DailyPlanCore.dll，请先运行 scripts/build.ps1 构建项目。",
                exception);
        }
        catch (BadImageFormatException exception)
        {
            throw new CoreException("本地核心 DailyPlanCore.dll 架构不匹配（需要 x64）。", exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new CoreException("本地核心版本不匹配，请重新构建 DailyPlanCore.dll。", exception);
        }
    }

    public string DatabasePath { get; }

    public IReadOnlyList<TaskItem> ListTasks(DateTime day)
    {
        var from = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);
        var to = from.AddDays(1);
        var fromText = FormatDateTime(from);
        var toText = FormatDateTime(to);

        lock (_gate)
        {
            EnsureNotDisposed();
            var pointer = Native.dp_list_tasks(_context, fromText, toText, out var errorPointer);
            var payload = ReadPayload(pointer, errorPointer, "读取任务失败");
            var tasks = Deserialize<List<TaskDto>>(payload, "任务数据格式无效");
            return tasks.Select(dto => dto.ToModel()).ToList();
        }
    }

    public TaskItem? GetTask(long id)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            var pointer = Native.dp_get_task(_context, id, out var errorPointer);
            var error = TakeError(errorPointer);
            if (pointer == IntPtr.Zero)
            {
                if (error is not null)
                {
                    throw new CoreException(error);
                }
                return null;
            }

            var payload = ReadAndFree(pointer);
            var dto = Deserialize<TaskDto>(payload, "任务数据格式无效");
            return dto.ToModel();
        }
    }

    public TaskItem CreateTask(TaskDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        lock (_gate)
        {
            EnsureNotDisposed();
            var json = SerializeDraft(draft);
            var pointer = Native.dp_create_task(_context, json, out var errorPointer);
            var payload = ReadPayload(pointer, errorPointer, "创建任务失败");
            var dto = Deserialize<TaskDto>(payload, "任务数据格式无效");
            return dto.ToModel();
        }
    }

    public TaskItem UpdateTask(long id, TaskDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        lock (_gate)
        {
            EnsureNotDisposed();
            var json = SerializeDraft(draft);
            var pointer = Native.dp_update_task(_context, id, json, out var errorPointer);
            var payload = ReadPayload(pointer, errorPointer, "更新任务失败");
            var dto = Deserialize<TaskDto>(payload, "任务数据格式无效");
            return dto.ToModel();
        }
    }

    public void DeleteTask(long id)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        lock (_gate)
        {
            EnsureNotDisposed();
            var ok = Native.dp_delete_task(_context, id, out var errorPointer);
            var error = TakeError(errorPointer);
            if (ok == 0)
            {
                throw new CoreException(error ?? "删除任务失败");
            }
        }
    }

    public TaskItem SetStatus(long id, TaskStatus status, DateTime? occurrenceDate = null)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        var dayText = occurrenceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        lock (_gate)
        {
            EnsureNotDisposed();
            var ok = Native.dp_set_status(
                _context, id, status.ToApiValue(), dayText, out var errorPointer);
            var error = TakeError(errorPointer);
            if (ok == 0)
            {
                throw new CoreException(error ?? "更新任务状态失败");
            }
        }

        if (occurrenceDate is DateTime selectedDay)
        {
            var occurrence = ListTasks(selectedDay).FirstOrDefault(task => task.Id == id);
            if (occurrence is not null)
            {
                return occurrence;
            }
        }

        return GetTask(id) ?? throw new CoreException("任务不存在");
    }

    /// <summary>
    /// Claims reminders that became due since the last poll. Events older than
    /// <paramref name="gracePeriod"/> are consumed without being returned.
    /// </summary>
    public IReadOnlyList<ReminderEvent> PollReminders(DateTime now, TimeSpan gracePeriod)
    {
        if (gracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        }

        var nowText = FormatDateTime(now);
        var staleText = FormatDateTime(now - gracePeriod);

        lock (_gate)
        {
            EnsureNotDisposed();
            var pointer = Native.dp_poll_reminders(_context, nowText, staleText, out var errorPointer);
            var payload = ReadPayload(pointer, errorPointer, "读取提醒失败");
            var reminders = Deserialize<List<ReminderDto>>(payload, "提醒数据格式无效");
            return reminders.Select(dto => dto.ToModel()).ToList();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_context != IntPtr.Zero)
            {
                Native.dp_destroy(_context);
                _context = IntPtr.Zero;
            }
        }
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string SerializeDraft(TaskDraft draft)
    {
        var payload = new TaskDto
        {
            Title = draft.Title,
            Notes = draft.Notes,
            StartTime = FormatDateTime(draft.StartTime),
            EndTime = FormatDateTime(draft.EndTime),
            Status = draft.Status.ToApiValue(),
            RepeatDaily = draft.RepeatDaily
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string FormatDateTime(DateTime value) =>
        value.ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);

    private static DateTime ParseDateTime(string? value, string fieldName)
    {
        if (DateTime.TryParseExact(
                value,
                LocalDateTimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var result))
        {
            return result;
        }

        throw new CoreException($"{fieldName}格式无效：{value}");
    }

    private static T Deserialize<T>(string json, string fallbackError)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                   ?? throw new CoreException(fallbackError);
        }
        catch (JsonException exception)
        {
            throw new CoreException(fallbackError, exception);
        }
    }

    private static string ReadPayload(IntPtr pointer, IntPtr errorPointer, string fallbackError)
    {
        var error = TakeError(errorPointer);
        if (pointer == IntPtr.Zero)
        {
            throw new CoreException(error ?? fallbackError);
        }

        var payload = ReadAndFree(pointer);
        if (error is not null)
        {
            throw new CoreException(error);
        }
        return payload;
    }

    private static string ReadAndFree(IntPtr pointer)
    {
        try
        {
            return Marshal.PtrToStringUTF8(pointer)
                   ?? throw new CoreException("本地核心返回了空数据");
        }
        finally
        {
            Native.dp_free_string(pointer);
        }
    }

    private static string? TakeError(IntPtr errorPointer)
    {
        if (errorPointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(errorPointer);
        }
        finally
        {
            Native.dp_free_string(errorPointer);
        }
    }

    private sealed class TaskDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("notes")]
        public string Notes { get; set; } = "";

        [JsonPropertyName("start_time")]
        public string StartTime { get; set; } = "";

        [JsonPropertyName("end_time")]
        public string EndTime { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("start_notified")]
        public bool StartNotified { get; set; }

        [JsonPropertyName("end_notified")]
        public bool EndNotified { get; set; }

        [JsonPropertyName("repeat_daily")]
        public bool RepeatDaily { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public string? UpdatedAt { get; set; }

        public TaskItem ToModel() => new(
            Id,
            Title,
            Notes,
            ParseDateTime(StartTime, "开始时间"),
            ParseDateTime(EndTime, "结束时间"),
            TaskStatusExtensions.ParseApiValue(Status),
            StartNotified,
            EndNotified,
            string.IsNullOrEmpty(CreatedAt) ? DateTime.Now : ParseDateTime(CreatedAt, "创建时间"),
            string.IsNullOrEmpty(UpdatedAt) ? DateTime.Now : ParseDateTime(UpdatedAt, "更新时间"),
            RepeatDaily);
    }

    private sealed class ReminderDto
    {
        [JsonPropertyName("task_id")]
        public long TaskId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("event")]
        public string EventName { get; set; } = "";

        [JsonPropertyName("due_at")]
        public string DueAt { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        public ReminderEvent ToModel() => new(
            TaskId,
            Title,
            EventName switch
            {
                "start" => ReminderKind.Start,
                "end" => ReminderKind.End,
                var value => throw new CoreException($"未知的提醒类型：{value}")
            },
            ParseDateTime(DueAt, "提醒时间"),
            TaskStatusExtensions.ParseApiValue(Status));
    }

    private static class Native
    {
        [DllImport(NativeLibrary, EntryPoint = "dp_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr dp_create(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string dbPath,
            out IntPtr errorMessage);

        [DllImport(NativeLibrary, EntryPoint = "dp_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void dp_destroy(IntPtr context);

        [DllImport(NativeLibrary, EntryPoint = "dp_free_string", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void dp_free_string(IntPtr value);

        [DllImport(NativeLibrary, EntryPoint = "dp_list_tasks", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr dp_list_tasks(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string fromIso,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string toIso,
            out IntPtr errorMessage);

        [DllImport(NativeLibrary, EntryPoint = "dp_get_task", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr dp_get_task(
            IntPtr context,
            long id,
            out IntPtr errorMessage);

        [DllImport(NativeLibrary, EntryPoint = "dp_create_task", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr dp_create_task(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string taskJson,
            out IntPtr errorMessage);

        [DllImport(NativeLibrary, EntryPoint = "dp_update_task", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr dp_update_task(
            IntPtr context,
            long id,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string taskJson,
            out IntPtr errorMessage);

        [DllImport(NativeLibrary, EntryPoint = "dp_delete_task", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int dp_delete_task(
            IntPtr context,
            long id,
            out IntPtr errorMessage);

        [DllImport(NativeLibrary, EntryPoint = "dp_set_status", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int dp_set_status(
            IntPtr context,
            long id,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string status,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? dayIso,
            out IntPtr errorMessage);

        [DllImport(NativeLibrary, EntryPoint = "dp_poll_reminders", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr dp_poll_reminders(
            IntPtr context,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string nowIso,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string staleBeforeIso,
            out IntPtr errorMessage);
    }
}
