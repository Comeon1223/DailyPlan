using DailyPlan.Client;

namespace DailyPlan.Client.Tests;

internal static class Program
{
    private static readonly DateTime TestDay = new(2026, 11, 4);
    private static int _assertions;

    private static int Main()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"DailyPlan-tests-{Guid.NewGuid():N}",
            "每日计划测试");

        try
        {
            Directory.CreateDirectory(root);
            Run("CRUD and persistence", () => CrudAndPersistence(root));
            Run("Reminder lifecycle", () => ReminderLifecycle(root));
            Run("Stale reminders", () => StaleReminders(root));
            Run("Validation", () => Validation(root));
            Run("Date filtering", () => DateFiltering(root));
            Run("Daily repeating tasks", () => RecurringTasks(root));
            Run("Daily repeating reminders", () => RecurringReminders(root));
            Run("Repeating cross-midnight window", () => RecurringCrossMidnight(root));

            Console.WriteLine($"PASS: {_assertions} assertions.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception.Message}");
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Temp files are harmless.
            }
        }
    }

    private static void Run(string name, Action test)
    {
        test();
        Console.WriteLine($"  ok  {name}");
    }

    private static void CrudAndPersistence(string root)
    {
        var dbPath = Path.Combine(root, "crud.db");
        long taskId;

        using (var core = new CoreClient(dbPath))
        {
            var dayStart = TestDay;
            Equal(0, core.ListTasks(dayStart).Count, "new database should be empty");

            var first = core.CreateTask(Draft(
                "写周报 & 复盘",
                "整理本周进展\n列出下周计划",
                TestDay.AddHours(9),
                TestDay.AddHours(10)));
            taskId = first.Id;
            Positive(first.Id, "task id should be assigned");
            Equal(TaskStatus.Pending, first.Status, "new task status");
            Equal("写周报 & 复盘", first.Title, "title round-trip");
            Equal("整理本周进展\n列出下周计划", first.Notes, "notes round-trip");

            core.CreateTask(Draft(
                "午间散步",
                "",
                TestDay.AddHours(12),
                TestDay.AddHours(12).AddMinutes(30),
                TaskStatus.InProgress));
            core.CreateTask(Draft(
                "阅读",
                "",
                TestDay.AddHours(15),
                TestDay.AddHours(16),
                TaskStatus.Completed));

            var tasks = core.ListTasks(dayStart);
            Equal(3, tasks.Count, "list count");
            Equal("写周报 & 复盘", tasks[0].Title, "list should be sorted by start time");
            Equal("午间散步", tasks[1].Title, "second task title");
            Equal(TaskStatus.InProgress, tasks[1].Status, "status round-trip");
            Equal(TaskStatus.Completed, tasks[2].Status, "completed status round-trip");

            var updated = core.UpdateTask(taskId, new TaskDraft(
                "写周报（修订）",
                "补充数据",
                TestDay.AddHours(9).AddMinutes(15),
                TestDay.AddHours(10).AddMinutes(15),
                TaskStatus.InProgress));
            Equal("写周报（修订）", updated.Title, "updated title");
            Equal(TaskStatus.InProgress, updated.Status, "updated status");
            Equal(TestDay.AddHours(9).AddMinutes(15), updated.StartTime, "updated start time");

            var completed = core.SetStatus(taskId, TaskStatus.Completed);
            Equal(TaskStatus.Completed, completed.Status, "set status");
            True(completed.IsCompleted, "IsCompleted helper");

            var loaded = core.GetTask(taskId);
            NotNull(loaded, "GetTask existing");
            Equal("补充数据", loaded!.Notes, "GetTask notes");

            core.DeleteTask(taskId);
            Null(core.GetTask(taskId), "deleted task should not exist");
            Equal(2, core.ListTasks(dayStart).Count, "list after delete");
        }

        using (var reopened = new CoreClient(dbPath))
        {
            var tasks = reopened.ListTasks(TestDay);
            Equal(2, tasks.Count, "tasks should persist after reopening the database");
            Equal("午间散步", tasks[0].Title, "persisted task title");
        }
    }

    private static void ReminderLifecycle(string root)
    {
        using var core = new CoreClient(Path.Combine(root, "reminders.db"));
        var task = core.CreateTask(new TaskDraft(
            "专注工作",
            "",
            TestDay.AddHours(10),
            TestDay.AddHours(10).AddMinutes(30),
            TaskStatus.Pending));

        var atStart = core.PollReminders(TestDay.AddHours(10).AddSeconds(1), TimeSpan.FromMinutes(2));
        Equal(1, atStart.Count, "start reminder count");
        Equal(ReminderKind.Start, atStart[0].Kind, "start reminder kind");
        Equal(task.Id, atStart[0].TaskId, "start reminder task");
        Equal("专注工作", atStart[0].Title, "start reminder title");

        var duplicate = core.PollReminders(TestDay.AddHours(10).AddSeconds(2), TimeSpan.FromMinutes(2));
        Equal(0, duplicate.Count, "start reminder must not repeat");

        var atEnd = core.PollReminders(TestDay.AddHours(10).AddMinutes(30).AddSeconds(1), TimeSpan.FromMinutes(2));
        Equal(1, atEnd.Count, "end reminder count");
        Equal(ReminderKind.End, atEnd[0].Kind, "end reminder kind");

        var noRepeat = core.PollReminders(TestDay.AddHours(10).AddMinutes(30).AddSeconds(2), TimeSpan.FromMinutes(2));
        Equal(0, noRepeat.Count, "end reminder must not repeat");

        // Completing a task before its end suppresses the end reminder.
        var second = core.CreateTask(new TaskDraft(
            "取消的任务",
            "",
            TestDay.AddHours(11),
            TestDay.AddHours(11).AddMinutes(30),
            TaskStatus.Pending));
        core.PollReminders(TestDay.AddHours(11).AddSeconds(1), TimeSpan.FromMinutes(2));
        core.SetStatus(second.Id, TaskStatus.Completed);
        var suppressed = core.PollReminders(
            TestDay.AddHours(11).AddMinutes(30).AddSeconds(1),
            TimeSpan.FromMinutes(2));
        Equal(0, suppressed.Count, "completed task should not produce an end reminder");
    }

    private static void StaleReminders(string root)
    {
        using var core = new CoreClient(Path.Combine(root, "stale.db"));
        core.CreateTask(new TaskDraft(
            "被错过的任务",
            "",
            TestDay.AddHours(8),
            TestDay.AddHours(9),
            TaskStatus.Pending));

        var stale = core.PollReminders(TestDay.AddHours(12), TimeSpan.FromMinutes(2));
        Equal(0, stale.Count, "old reminders should be consumed silently");

        var next = core.PollReminders(TestDay.AddHours(12).AddMinutes(1), TimeSpan.FromMinutes(2));
        Equal(0, next.Count, "consumed reminders should not reappear");
    }

    private static void Validation(string root)
    {
        using var core = new CoreClient(Path.Combine(root, "validation.db"));

        Throws<CoreException>(
            () => core.CreateTask(new TaskDraft(
                "错误时间",
                "",
                TestDay.AddHours(10),
                TestDay.AddHours(9),
                TaskStatus.Pending)),
            "end before start");

        Throws<CoreException>(
            () => core.CreateTask(new TaskDraft(
                " ",
                "",
                TestDay.AddHours(10),
                TestDay.AddHours(11),
                TaskStatus.Pending)),
            "empty title");

        Throws<ArgumentOutOfRangeException>(
            () => core.PollReminders(TestDay.AddHours(10), TimeSpan.FromMinutes(-1)),
            "negative reminder grace");

        Throws<CoreException>(
            () => core.DeleteTask(99999),
            "delete missing task");

        Throws<CoreException>(
            () => core.CreateTask(new TaskDraft(
                "超过一天",
                "",
                TestDay.AddHours(9),
                TestDay.AddDays(2).AddHours(9),
                TaskStatus.Pending,
                RepeatDaily: true)),
            "repeating window longer than 24 hours");
    }

    private static void DateFiltering(string root)
    {
        using var core = new CoreClient(Path.Combine(root, "dates.db"));
        core.CreateTask(Draft("今天", "", TestDay.AddHours(9), TestDay.AddHours(10)));
        core.CreateTask(Draft("明天", "", TestDay.AddDays(1).AddHours(9), TestDay.AddDays(1).AddHours(10)));

        var tasks = core.ListTasks(TestDay);
        Equal(1, tasks.Count, "only tasks on the selected day are listed");
        Equal("今天", tasks[0].Title, "day filter title");

        var tomorrow = core.ListTasks(TestDay.AddDays(1));
        Equal(1, tomorrow.Count, "next day filter");
        Equal("明天", tomorrow[0].Title, "next day title");
    }

    private static void RecurringTasks(string root)
    {
        using var core = new CoreClient(Path.Combine(root, "recurring.db"));
        var task = core.CreateTask(new TaskDraft(
            "每日阅读",
            "",
            TestDay.AddHours(20),
            TestDay.AddHours(20).AddMinutes(30),
            TaskStatus.Completed,
            RepeatDaily: true));

        True(task.RepeatDaily, "created task should be repeating");
        True(task.IsRecurring, "IsRecurring helper");
        Equal(TaskStatus.Pending, task.Status, "repeating task starts pending");

        Equal(0, core.ListTasks(TestDay.AddDays(-1)).Count,
            "repeating task should not appear before its first date");
        Equal(1, core.ListTasks(TestDay).Count,
            "repeating task appears on its first date");

        var laterDay = TestDay.AddDays(3);
        var later = core.ListTasks(laterDay);
        Equal(1, later.Count, "repeating task appears on later dates");
        Equal(laterDay.AddHours(20), later[0].StartTime,
            "repeating occurrence start time");
        Equal(laterDay.AddHours(20).AddMinutes(30), later[0].EndTime,
            "repeating occurrence end time");
        Equal(TaskStatus.Pending, later[0].Status,
            "each repeating occurrence starts pending");

        var completed = core.SetStatus(later[0].Id, TaskStatus.Completed, laterDay);
        Equal(TaskStatus.Completed, completed.Status,
            "repeating occurrence status update");
        Equal(TaskStatus.Completed, core.ListTasks(laterDay)[0].Status,
            "completion is visible on the selected day");
        Equal(TaskStatus.Pending, core.ListTasks(TestDay)[0].Status,
            "completion does not change other days");
    }

    private static void RecurringReminders(string root)
    {
        using var core = new CoreClient(Path.Combine(root, "recurring-reminders.db"));
        var task = core.CreateTask(new TaskDraft(
            "每日专注",
            "",
            TestDay.AddHours(20),
            TestDay.AddHours(20).AddMinutes(30),
            TaskStatus.Pending,
            RepeatDaily: true));

        var nextDay = TestDay.AddDays(1);
        var start = core.PollReminders(
            nextDay.AddHours(20).AddSeconds(1), TimeSpan.FromMinutes(2));
        Equal(1, start.Count, "repeating start reminder count");
        Equal(ReminderKind.Start, start[0].Kind, "repeating start reminder kind");
        Equal(task.Id, start[0].TaskId, "repeating start reminder task");
        Equal(nextDay.AddHours(20), start[0].DueAt, "repeating start reminder time");

        var duplicate = core.PollReminders(
            nextDay.AddHours(20).AddSeconds(2), TimeSpan.FromMinutes(2));
        Equal(0, duplicate.Count, "repeating start reminder must not repeat");

        var end = core.PollReminders(
            nextDay.AddHours(20).AddMinutes(30).AddSeconds(1),
            TimeSpan.FromMinutes(2));
        Equal(1, end.Count, "repeating end reminder count");
        Equal(ReminderKind.End, end[0].Kind, "repeating end reminder kind");

        var noRepeat = core.PollReminders(
            nextDay.AddHours(20).AddMinutes(30).AddSeconds(2),
            TimeSpan.FromMinutes(2));
        Equal(0, noRepeat.Count, "repeating end reminder must not repeat");

        var followingDay = nextDay.AddDays(1);
        var followingStart = core.PollReminders(
            followingDay.AddHours(20).AddSeconds(1), TimeSpan.FromMinutes(2));
        Equal(1, followingStart.Count, "reminder fires again on the next day");

        core.SetStatus(task.Id, TaskStatus.Completed, followingDay);
        var suppressed = core.PollReminders(
            followingDay.AddHours(20).AddMinutes(30).AddSeconds(1),
            TimeSpan.FromMinutes(2));
        Equal(0, suppressed.Count,
            "completed repeating occurrence should not produce an end reminder");
    }

    private static void RecurringCrossMidnight(string root)
    {
        using var core = new CoreClient(Path.Combine(root, "recurring-night.db"));
        core.CreateTask(new TaskDraft(
            "夜间阅读",
            "",
            TestDay.AddHours(23),
            TestDay.AddDays(1).AddHours(1),
            TaskStatus.Pending,
            RepeatDaily: true));

        var listed = core.ListTasks(TestDay);
        Equal(1, listed.Count, "cross-midnight repeating task listed");
        Equal(TestDay.AddHours(23), listed[0].StartTime,
            "cross-midnight occurrence start");
        Equal(TestDay.AddDays(1).AddHours(1), listed[0].EndTime,
            "cross-midnight occurrence end");

        var end = core.PollReminders(
            TestDay.AddDays(1).AddHours(1).AddSeconds(1), TimeSpan.FromMinutes(2));
        Equal(1, end.Count, "cross-midnight repeating end reminder");
        Equal(ReminderKind.End, end[0].Kind, "cross-midnight reminder kind");
    }

    private static TaskDraft Draft(
        string title,
        string notes,
        DateTime start,
        DateTime end,
        TaskStatus status = TaskStatus.Pending) =>
        new(title, notes, start, end, status);

    private static void Equal<T>(T expected, T actual, string message)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message}: expected '{expected}', actual '{actual}'");
        }
    }

    private static void True(bool condition, string message)
    {
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Positive(long value, string message)
    {
        _assertions++;
        if (value <= 0)
        {
            throw new InvalidOperationException($"{message}: {value}");
        }
    }

    private static void NotNull(object? value, string message)
    {
        _assertions++;
        if (value is null)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Null(object? value, string message)
    {
        _assertions++;
        if (value is not null)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        _assertions++;
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"{message}: expected {typeof(TException).Name}");
    }
}
