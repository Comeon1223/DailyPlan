using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DailyPlan.Client;
using TaskStatus = DailyPlan.Client.TaskStatus;

namespace DailyPlan.App;

public partial class TaskEditorWindow : Window
{
    // True while the dialog is being populated so XAML event handlers do not
    // touch controls that have not been initialised yet.
    private bool _initializing = true;

    public TaskEditorWindow(TaskItem? existing, DateTime defaultDate)
    {
        InitializeComponent();

        StatusCombo.ItemsSource = TaskStatusCatalog.All;
        StatusCombo.DisplayMemberPath = nameof(TaskStatusOption.Label);
        StatusCombo.SelectedValuePath = nameof(TaskStatusOption.Value);

        if (existing is null)
        {
            Title = "新建任务";
            DialogTitle.Text = "新建任务";
            DialogSubtitle.Text = "给这段时间一个明确的安排。";

            var start = CreateDefaultStart(defaultDate);
            var end = start.AddHours(1);
            StartDatePicker.SelectedDate = start.Date;
            StartTimeBox.Text = start.ToString("HH:mm", CultureInfo.InvariantCulture);
            EndDatePicker.SelectedDate = end.Date;
            EndTimeBox.Text = end.ToString("HH:mm", CultureInfo.InvariantCulture);
            StatusCombo.SelectedValue = TaskStatus.Pending;
        }
        else
        {
            Title = "编辑任务";
            DialogTitle.Text = "编辑任务";
            DialogSubtitle.Text = "调整安排，让计划更贴合当下。";

            TitleBox.Text = existing.Title;
            NotesBox.Text = existing.Notes;
            StartDatePicker.SelectedDate = existing.StartTime.Date;
            StartTimeBox.Text = existing.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            EndDatePicker.SelectedDate = existing.EndTime.Date;
            EndTimeBox.Text = existing.EndTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            StatusCombo.SelectedValue = existing.Status;
            RepeatDailyCheck.IsChecked = existing.RepeatDaily;
        }

        StartDatePicker.SelectedDateChanged += StartDatePicker_SelectedDateChanged;
        _initializing = false;
        UpdateRepeatUi();

        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    public TaskDraft? Result { get; private set; }

    private static DateTime CreateDefaultStart(DateTime date)
    {
        var day = date.Date;
        if (day != DateTime.Today)
        {
            return day.AddHours(9);
        }

        var now = DateTime.Now;
        var totalMinutes = now.Hour * 60 + now.Minute;
        var nextSlot = (totalMinutes / 30 + 1) * 30;
        return day.AddMinutes(nextSlot);
    }

    private void StartDatePicker_SelectedDateChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (RepeatDailyCheck.IsChecked == true)
        {
            SyncRepeatEndDate();
            return;
        }

        if (StartDatePicker.SelectedDate is DateTime startDate &&
            EndDatePicker.SelectedDate is DateTime endDate &&
            endDate < startDate)
        {
            EndDatePicker.SelectedDate = startDate;
        }
    }

    private void RepeatDailyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        UpdateRepeatUi();
    }

    private void RepeatTimeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing || RepeatDailyCheck.IsChecked != true)
        {
            return;
        }

        SyncRepeatEndDate();
    }

    private void UpdateRepeatUi()
    {
        var repeat = RepeatDailyCheck.IsChecked == true;
        StatusCombo.IsEnabled = !repeat;
        EndDatePicker.IsEnabled = !repeat;
        StartDateLabel.Text = repeat ? "首次日期" : "开始时间";
        EndDateLabel.Text = repeat ? "结束时间（每天）" : "结束时间";
        StatusHintText.Text = repeat
            ? "重复任务在设定的时间段内每天提醒，每天单独记录完成状态。"
            : "结束时间需晚于开始时间，也可以跨天。";

        if (repeat)
        {
            StatusCombo.SelectedValue = TaskStatus.Pending;
            SyncRepeatEndDate();
        }
    }

    private void SyncRepeatEndDate()
    {
        if (StartDatePicker.SelectedDate is not DateTime startDate ||
            !TryParseTime(StartTimeBox.Text, out var startTime) ||
            !TryParseTime(EndTimeBox.Text, out var endTime))
        {
            return;
        }

        var end = startDate.Date.Add(endTime);
        if (end <= startDate.Date.Add(startTime))
        {
            end = end.AddDays(1);
        }
        EndDatePicker.SelectedDate = end.Date;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        HideValidation();

        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            ShowValidation("请输入任务名称。", TitleBox);
            return;
        }

        if (StartDatePicker.SelectedDate is not DateTime startDate)
        {
            ShowValidation("请选择开始日期。", StartDatePicker);
            return;
        }

        if (!TryParseTime(StartTimeBox.Text, out var startTime))
        {
            ShowValidation("开始时间格式无效，请使用 24 小时制，例如 09:30。", StartTimeBox);
            return;
        }

        if (!TryParseTime(EndTimeBox.Text, out var endTime))
        {
            ShowValidation("结束时间格式无效，请使用 24 小时制，例如 10:30。", EndTimeBox);
            return;
        }

        var repeatDaily = RepeatDailyCheck.IsChecked == true;
        var start = startDate.Date.Add(startTime);
        DateTime end;
        TaskStatus status;

        if (repeatDaily)
        {
            // Repeating tasks use a daily time window; the end date is derived
            // from the times and may roll over to the next day.
            end = startDate.Date.Add(endTime);
            if (end <= start)
            {
                end = end.AddDays(1);
            }
            status = TaskStatus.Pending;
        }
        else
        {
            if (EndDatePicker.SelectedDate is not DateTime endDate)
            {
                ShowValidation("请选择结束日期。", EndDatePicker);
                return;
            }

            end = endDate.Date.Add(endTime);
            if (end <= start)
            {
                ShowValidation("结束时间必须晚于开始时间。", EndTimeBox);
                return;
            }

            status = StatusCombo.SelectedValue is TaskStatus selectedStatus
                ? selectedStatus
                : TaskStatus.Pending;
        }

        Result = new TaskDraft(
            title, NotesBox.Text.Trim(), start, end, status, repeatDaily);
        DialogResult = true;
    }

    private static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        if (!TimeOnly.TryParseExact(
                text.Trim(),
                ["H:mm", "HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        time = parsed.ToTimeSpan();
        return true;
    }

    private void ShowValidation(string message, Control focusTarget)
    {
        ValidationText.Text = message;
        ValidationBorder.Visibility = Visibility.Visible;
        focusTarget.Focus();
    }

    private void HideValidation()
    {
        ValidationBorder.Visibility = Visibility.Collapsed;
        ValidationText.Text = "";
    }
}
