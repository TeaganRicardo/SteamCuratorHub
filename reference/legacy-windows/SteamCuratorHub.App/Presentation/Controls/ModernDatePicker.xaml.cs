using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SteamCuratorHub.Presentation.Controls;

public partial class ModernDatePicker : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
        nameof(SelectedDate),
        typeof(DateTime?),
        typeof(ModernDatePicker),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectedDateChanged));

    public static readonly DependencyProperty CultureNameProperty = DependencyProperty.Register(
        nameof(CultureName),
        typeof(string),
        typeof(ModernDatePicker),
        new PropertyMetadata("en-US", OnCultureNameChanged));

    private DateTime _displayMonth = FirstDayOfMonth(DateTime.Today);
    private CultureInfo _culture = CultureInfo.GetCultureInfo("en-US");

    public ModernDatePicker()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshCalendar();
        Unloaded += (_, _) => CalendarPopup.IsOpen = false;
        ApplyCulture(CultureName);
    }

    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    public string CultureName
    {
        get => (string)GetValue(CultureNameProperty);
        set => SetValue(CultureNameProperty, value);
    }

    public ObservableCollection<string> WeekdayHeaders { get; } = [];

    public ObservableCollection<CalendarDayItem> CalendarDays { get; } = [];

    private static void OnSelectedDateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        var picker = (ModernDatePicker)dependencyObject;
        if (eventArgs.NewValue is DateTime selectedDate)
            picker._displayMonth = FirstDayOfMonth(selectedDate);

        picker.RefreshCalendar();
    }

    private static void OnCultureNameChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        var picker = (ModernDatePicker)dependencyObject;
        picker.ApplyCulture(eventArgs.NewValue as string);
        picker.RefreshCalendar();
    }

    private void ApplyCulture(string? cultureName)
    {
        try
        {
            _culture = CultureInfo.GetCultureInfo(
                string.IsNullOrWhiteSpace(cultureName) ? "en-US" : cultureName);
        }
        catch (CultureNotFoundException)
        {
            _culture = CultureInfo.GetCultureInfo("en-US");
        }
    }

    private void RefreshCalendar()
    {
        if (DisplayDateTextBlock is null || MonthTitleTextBlock is null || TodayButton is null)
            return;

        DisplayDateTextBlock.Text = SelectedDate is DateTime selectedDate
            ? FormatCompactDate(selectedDate)
            : (_culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "选择日期" : "Select a date");

        DisplayButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            _culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "选择日期" : "Select date");
        MonthTitleTextBlock.Text = _displayMonth.ToString("Y", _culture);
        TodayButton.Content = _culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "今天" : "Today";

        BuildWeekdayHeaders();
        BuildCalendarDays();
    }

    private void BuildWeekdayHeaders()
    {
        WeekdayHeaders.Clear();
        var firstDay = (int)_culture.DateTimeFormat.FirstDayOfWeek;
        var shortestNames = _culture.DateTimeFormat.ShortestDayNames;

        for (var offset = 0; offset < 7; offset++)
        {
            var dayIndex = (firstDay + offset) % 7;
            WeekdayHeaders.Add(shortestNames[dayIndex]);
        }
    }

    private void BuildCalendarDays()
    {
        CalendarDays.Clear();
        var firstDayOffset = (7 + (int)_displayMonth.DayOfWeek - (int)_culture.DateTimeFormat.FirstDayOfWeek) % 7;
        var gridStart = _displayMonth.AddDays(-firstDayOffset);
        var today = DateTime.Today;
        var selected = SelectedDate?.Date;

        for (var index = 0; index < 42; index++)
        {
            var date = gridStart.AddDays(index);
            CalendarDays.Add(new CalendarDayItem(
                date,
                date.Day.ToString(_culture),
                date.Month == _displayMonth.Month && date.Year == _displayMonth.Year,
                date == today,
                date == selected,
                date.ToString("D", _culture)));
        }
    }

    private string FormatCompactDate(DateTime date) =>
        _culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? date.ToString("yyyy年M月d日", _culture)
            : date.ToString("MMM d, yyyy", _culture);

    private static DateTime FirstDayOfMonth(DateTime date) => new(date.Year, date.Month, 1);

    private void DisplayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CalendarPopup.IsOpen)
        {
            _displayMonth = FirstDayOfMonth(SelectedDate ?? DateTime.Today);
            RefreshCalendar();
        }

        CalendarPopup.IsOpen = !CalendarPopup.IsOpen;
    }

    private void PreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(-1);
        RefreshCalendar();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(1);
        RefreshCalendar();
    }

    private void DayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DateTime date })
            return;

        SetCurrentValue(SelectedDateProperty, (DateTime?)date.Date);
        CalendarPopup.IsOpen = false;
        DisplayButton.Focus();
    }

    private void TodayButton_Click(object sender, RoutedEventArgs e)
    {
        SetCurrentValue(SelectedDateProperty, (DateTime?)DateTime.Today);
        CalendarPopup.IsOpen = false;
        DisplayButton.Focus();
    }

    private void CalendarPopup_Opened(object? sender, EventArgs e) => RefreshCalendar();

    private void CalendarPopup_Closed(object? sender, EventArgs e) => DisplayButton.Focus();

    private void PickerRoot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !CalendarPopup.IsOpen)
            return;

        CalendarPopup.IsOpen = false;
        e.Handled = true;
    }
}

public sealed record CalendarDayItem(
    DateTime Date,
    string DayNumber,
    bool IsCurrentMonth,
    bool IsToday,
    bool IsSelected,
    string AccessibleDate);
