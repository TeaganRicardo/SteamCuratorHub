using System.Globalization;
using System.Windows;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.App;

public partial class CreateTaskDialog : Window
{
    private readonly bool _chinese;
    private readonly bool _editing;
    private readonly DateTime _initialDueDate;
    public double TargetHours { get; private set; } = 4;
    public DateTime DueDate { get; private set; } = DateTime.Today.AddDays(7);

    public CreateTaskDialog(Game game, string language = "zh-CN")
        : this(game, 4, DateTime.Today.AddDays(7), language, editing: false)
    {
    }

    public CreateTaskDialog(ReviewTask task, string language = "zh-CN")
        : this(task.Game, task.TargetHours, task.DueDate, language, editing: true)
    {
    }

    private CreateTaskDialog(Game game, double targetHours, DateTime dueDate, string language, bool editing)
    {
        InitializeComponent();
        _chinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        _editing = editing;
        _initialDueDate = dueDate.Date;
        TargetHours = targetHours;
        DueDate = dueDate.Date;
        Title = editing
            ? (_chinese ? "任务设置" : "Task settings")
            : (_chinese ? "创建测评任务" : "Create review task");
        HeaderLabel.Text = Title;
        TargetHoursLabel.Text = _chinese ? "目标时长" : "Target hours";
        DueDateLabel.Text = _chinese ? "提交截至" : "Due date";
        CancelButton.Content = _chinese ? "取消" : "Cancel";
        CreateButton.Content = editing
            ? (_chinese ? "保存修改" : "Save changes")
            : (_chinese ? "创建" : "Create");
        CloseButton.ToolTip = _chinese ? "关闭" : "Close";
        GameLabel.Text = game.GetName(language);
        TargetHoursBox.Text = targetHours.ToString("0.##", CultureInfo.GetCultureInfo(_chinese ? "zh-CN" : "en-US"));
        DueDatePicker.CultureName = language;
        DueDatePicker.SelectedDate = DueDate;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var text = TargetHoursBox.Text?.Trim();
        var parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var hours)
                     || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out hours);
        if (!parsed || !double.IsFinite(hours) || hours <= 0 || hours > 1000)
        {
            AppMessageDialog.Show(
                this,
                _chinese ? "目标时长需大于 0，且不超过 1000 小时。" : "Target hours must be greater than 0 and no more than 1000.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information,
                _chinese);
            return;
        }

        DueDate = DueDatePicker.SelectedDate?.Date ?? DateTime.Today.AddDays(7);
        if (DueDate < DateTime.Today && (!_editing || DueDate != _initialDueDate))
        {
            AppMessageDialog.Show(
                this,
                _chinese ? "提交截至不能早于今天。" : "Due date cannot be earlier than today.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information,
                _chinese);
            return;
        }
        TargetHours = hours;
        DialogResult = true;
    }
}
