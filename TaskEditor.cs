using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Daynote
{
    /// <summary>A separate top-level dialog; it must not inherit the desktop card's native parent.</summary>
    public sealed class TaskEditor : Window
    {
        private readonly TaskItem original;
        private readonly TextBox titleBox;
        private readonly TextBox notesBox;
        private readonly TextBox timeBox;
        private readonly TextBox endTimeBox;
        private readonly DatePicker datePicker;
        private readonly DatePicker endDatePicker;
        private readonly CheckBox dateEnabled;
        private readonly CheckBox timeEnabled;
        private readonly CheckBox endEnabled;
        private readonly ComboBox reminderBox;
        private readonly ComboBox colorBox;
        private readonly TextBlock errorText;
        private readonly TextBlock dateHelp;
        private readonly Brush textBrush;
        private readonly Brush mutedBrush;
        private readonly Brush inputBrush;
        private readonly Brush lineBrush;
        private readonly int[] reminderValues = { -1, 0, 5, 15, 30, 60, 1440 };
        private readonly string[] colorIds = { "blue", "purple", "green", "orange", "teal", "red" };
        private bool dateParseFailed;
        private bool endDateParseFailed;

        public TaskItem Result { get; private set; }

        public TaskEditor(TaskItem original, bool darkMode, DateTime? initialDate = null)
        {
            this.original = original;
            Title = original == null ? "添加提醒" : "编辑提醒";
            Width = 520;
            Height = Math.Min(790, SystemParameters.WorkArea.Height - 48);
            MinWidth = 440;
            MinHeight = Math.Min(520, Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;
            Background = Brush(darkMode ? "#1C1C1E" : "#FFFFFF");
            textBrush = Brush(darkMode ? "#F5F5F7" : "#1D1D1F");
            mutedBrush = Brush(darkMode ? "#98989D" : "#86868B");
            inputBrush = Brush(darkMode ? "#2C2C2E" : "#F5F5F7");
            lineBrush = Brush(darkMode ? "#48484A" : "#E5E5EA");
            Foreground = textBrush;
            Language = System.Windows.Markup.XmlLanguage.GetLanguage("zh-CN");

            var outer = new Grid { Margin = new Thickness(24, 22, 24, 20) };
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = outer;

            var form = new StackPanel();
            var scroll = new ScrollViewer
            {
                Content = form,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 0, 6, 0)
            };
            outer.Children.Add(scroll);

            form.Children.Add(new TextBlock
            {
                Text = original == null ? "新的日程" : "编辑日程",
                Foreground = textBrush,
                FontSize = 23,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5)
            });
            form.Children.Add(new TextBlock
            {
                Text = "安排好时间，把值得记住的事留在这里。",
                Foreground = mutedBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20)
            });

            titleBox = MakeTextBox("事项名称", 200);
            titleBox.Text = original == null ? "" : original.Title ?? "";
            form.Children.Add(Label("事项名称 *"));
            form.Children.Add(titleBox);

            dateEnabled = MakeCheckBox("设置日期", original == null || original.DueAt.HasValue);
            dateEnabled.Margin = new Thickness(0, 19, 0, 10);
            form.Children.Add(dateEnabled);

            DateTime now = DateTime.Now;
            DateTime next = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0)
                .AddMinutes(30 - now.Minute % 30);
            DateTime initial = next;
            if (initialDate.HasValue)
            {
                DateTime requested = initialDate.Value;
                if (requested.TimeOfDay != TimeSpan.Zero)
                    initial = new DateTime(requested.Year, requested.Month, requested.Day, requested.Hour, requested.Minute, 0);
                else if (requested.Date == now.Date)
                    initial = next.Date == requested.Date ? next : requested.Date.AddHours(23).AddMinutes(59);
                else initial = requested.Date.AddHours(9);
            }
            if (original != null && original.DueAt.HasValue) initial = original.DueAt.Value;

            var schedule = new Grid();
            schedule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            schedule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            schedule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            form.Children.Add(schedule);

            var dateGroup = new StackPanel();
            dateGroup.Children.Add(Label("开始日期"));
            datePicker = new DatePicker
            {
                SelectedDate = initial.Date,
                SelectedDateFormat = DatePickerFormat.Short,
                Height = 36,
                FontSize = 14,
                Background = inputBrush,
                Foreground = textBrush,
                BorderBrush = lineBrush,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            // The calendar popup uses Windows' standard light palette for readability.
            var calendarStyle = new Style(typeof(System.Windows.Controls.Calendar));
            calendarStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
            calendarStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
            datePicker.CalendarStyle = calendarStyle;
            AutomationProperties.SetName(datePicker, "开始日期");
            datePicker.DateValidationError += delegate(object sender, DatePickerDateValidationErrorEventArgs e)
            {
                e.ThrowException = false;
                dateParseFailed = true;
                ShowError("请填写有效日期，例如 2026/10/26。", datePicker);
            };
            datePicker.SelectedDateChanged += delegate { dateParseFailed = false; };
            datePicker.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(delegate { dateParseFailed = false; }));
            dateGroup.Children.Add(datePicker);
            schedule.Children.Add(dateGroup);

            var timeGroup = new StackPanel();
            timeEnabled = MakeCheckBox("具体时间", original == null || original.HasTime);
            timeEnabled.Margin = new Thickness(0, 0, 0, 7);
            timeGroup.Children.Add(timeEnabled);
            timeBox = MakeTextBox("开始时间（24小时制）", 5);
            timeBox.Text = initial.ToString("HH:mm", CultureInfo.InvariantCulture);
            timeBox.ToolTip = "24 小时制，例如 09:30 或 18:00";
            timeGroup.Children.Add(timeBox);
            Grid.SetColumn(timeGroup, 2);
            schedule.Children.Add(timeGroup);

            dateHelp = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = mutedBrush,
                FontSize = 11,
                Margin = new Thickness(0, 7, 0, 16)
            };
            form.Children.Add(dateHelp);

            endEnabled = MakeCheckBox("设置结束时间", original != null && original.HasTime && original.EndAt.HasValue);
            endEnabled.Margin = new Thickness(0, 0, 0, 10);
            form.Children.Add(endEnabled);
            DateTime initialEnd = original != null && original.EndAt.HasValue ? original.EndAt.Value
                : initial <= DateTime.MaxValue.AddHours(-1) ? initial.AddHours(1) : initial;
            var endSchedule = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            endSchedule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            endSchedule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            endSchedule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            var endDateGroup = new StackPanel();
            endDateGroup.Children.Add(Label("结束日期"));
            endDatePicker = new DatePicker
            {
                SelectedDate = initialEnd.Date,
                SelectedDateFormat = DatePickerFormat.Short,
                Height = 36,
                FontSize = 14,
                Background = inputBrush,
                Foreground = textBrush,
                BorderBrush = lineBrush,
                VerticalContentAlignment = VerticalAlignment.Center,
                CalendarStyle = calendarStyle
            };
            AutomationProperties.SetName(endDatePicker, "结束日期");
            endDatePicker.DateValidationError += delegate(object sender, DatePickerDateValidationErrorEventArgs e)
            {
                e.ThrowException = false;
                endDateParseFailed = true;
                ShowError("请填写有效的结束日期。", endDatePicker);
            };
            endDatePicker.SelectedDateChanged += delegate { endDateParseFailed = false; };
            endDatePicker.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(delegate { endDateParseFailed = false; }));
            endDateGroup.Children.Add(endDatePicker);
            endSchedule.Children.Add(endDateGroup);
            var endTimeGroup = new StackPanel();
            endTimeGroup.Children.Add(Label("结束时间"));
            endTimeBox = MakeTextBox("结束时间（24小时制）", 5);
            endTimeBox.Text = initialEnd.ToString("HH:mm", CultureInfo.InvariantCulture);
            endTimeBox.ToolTip = "结束时间必须晚于开始时间；跨夜日程请同时选择次日日期。";
            endTimeGroup.Children.Add(endTimeBox);
            Grid.SetColumn(endTimeGroup, 2);
            endSchedule.Children.Add(endTimeGroup);
            form.Children.Add(endSchedule);
            form.Children.Add(new TextBlock
            {
                Text = "结束时间可不填；跨夜日程请选择下一天的结束日期。",
                Foreground = mutedBrush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 17)
            });

            var optionsGrid = new Grid();
            optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            optionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            var reminderGroup = new StackPanel();
            reminderGroup.Children.Add(Label("提醒时间"));
            reminderBox = new ComboBox { Height = 36, FontSize = 13, Padding = new Thickness(8, 4, 8, 4) };
            // Keep the system ComboBox palette internally consistent, including its popup.
            reminderBox.Foreground = Brushes.Black;
            AutomationProperties.SetName(reminderBox, "提醒时间");
            string[] options = { "不提醒", "准时提醒", "提前 5 分钟", "提前 15 分钟", "提前 30 分钟", "提前 1 小时", "提前 1 天" };
            foreach (string option in options) reminderBox.Items.Add(option);
            int selected = original == null ? 1 : Array.IndexOf(reminderValues, original.ReminderMinutes);
            // Preserve a valid custom interval when opening data created by a newer version.
            if (selected < 0 && original != null && original.ReminderMinutes >= 0)
            {
                reminderBox.Items.Add("提前 " + original.ReminderMinutes.ToString(CultureInfo.InvariantCulture) + " 分钟");
                selected = reminderValues.Length;
            }
            reminderBox.SelectedIndex = Math.Max(0, selected);
            reminderGroup.Children.Add(reminderBox);
            optionsGrid.Children.Add(reminderGroup);

            var colorGroup = new StackPanel();
            colorGroup.Children.Add(Label("日程颜色"));
            colorBox = new ComboBox { Height = 36, FontSize = 13, Padding = new Thickness(8, 4, 8, 4), Foreground = Brushes.Black };
            string[] colorNames = { "蓝色", "紫色", "绿色", "橙色", "青色", "红色" };
            string[] colorHexes = { "#007AFF", "#AF52DE", "#34C759", "#FF9500", "#30B0C7", "#FF3B30" };
            for (int i = 0; i < colorIds.Length; i++)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Brush(colorHexes[i]), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                line.Children.Add(new TextBlock { Text = colorNames[i], Foreground = Brushes.Black });
                var item = new ComboBoxItem { Content = line, Tag = colorIds[i] };
                AutomationProperties.SetName(item, colorNames[i]);
                colorBox.Items.Add(item);
            }
            colorBox.SelectedIndex = Math.Max(0, Array.IndexOf(colorIds, original == null ? "blue" : original.ColorId));
            AutomationProperties.SetName(colorBox, "日程颜色");
            colorGroup.Children.Add(colorBox);
            Grid.SetColumn(colorGroup, 2);
            optionsGrid.Children.Add(colorGroup);
            form.Children.Add(optionsGrid);

            var notesLabel = Label("备注");
            notesLabel.Margin = new Thickness(0, 17, 0, 7);
            form.Children.Add(notesLabel);
            notesBox = MakeTextBox("备注", 4000);
            notesBox.Text = original == null ? "" : original.Notes ?? "";
            notesBox.AcceptsReturn = true;
            notesBox.TextWrapping = TextWrapping.Wrap;
            notesBox.VerticalContentAlignment = VerticalAlignment.Top;
            notesBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            notesBox.MinHeight = 100;
            notesBox.Height = 100;
            form.Children.Add(notesBox);

            errorText = new TextBlock
            {
                Foreground = Brush(darkMode ? "#FFB4A9" : "#B42318"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
                MinHeight = 18
            };
            AutomationProperties.SetLiveSetting(errorText, AutomationLiveSetting.Assertive);
            form.Children.Add(errorText);

            var actions = new Grid { Margin = new Thickness(0, 17, 0, 0) };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cancel = MakeButton("取消", false);
            cancel.IsCancel = true;
            cancel.Click += delegate { DialogResult = false; };
            Grid.SetColumn(cancel, 1);
            actions.Children.Add(cancel);
            var save = MakeButton("保存提醒", true);
            save.Click += delegate { Save(); };
            Grid.SetColumn(save, 3);
            actions.Children.Add(save);
            Grid.SetRow(actions, 1);
            outer.Children.Add(actions);

            dateEnabled.Checked += delegate { UpdateEnabled(); };
            dateEnabled.Unchecked += delegate { UpdateEnabled(); };
            timeEnabled.Checked += delegate { UpdateEnabled(); };
            timeEnabled.Unchecked += delegate { UpdateEnabled(); };
            endEnabled.Checked += delegate { UpdateEnabled(); };
            endEnabled.Unchecked += delegate { UpdateEnabled(); };
            UpdateEnabled();
            Loaded += delegate { titleBox.Focus(); titleBox.SelectAll(); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    e.Handled = true;
                    Save();
                }
            };
        }

        private void UpdateEnabled()
        {
            bool hasDate = dateEnabled.IsChecked == true;
            datePicker.IsEnabled = hasDate;
            timeEnabled.IsEnabled = hasDate;
            timeBox.IsEnabled = hasDate && timeEnabled.IsChecked == true;
            endEnabled.IsEnabled = hasDate && timeEnabled.IsChecked == true;
            endDatePicker.IsEnabled = endEnabled.IsEnabled && endEnabled.IsChecked == true;
            endTimeBox.IsEnabled = endDatePicker.IsEnabled;
            reminderBox.IsEnabled = hasDate;
            dateHelp.Text = !hasDate ? "未设置日期的事项不会发送定时提醒。"
                : timeEnabled.IsChecked == true ? "使用电脑的本地时间，按 24 小时制填写。"
                : "全天事项显示为“全天”；开启提醒时以当天 09:00 为准。";
        }

        private void Save()
        {
            string title = titleBox.Text.Trim();
            if (title.Length == 0)
            {
                ShowError("请先填写事项名称。", titleBox);
                return;
            }
            DateTime? due = null;
            DateTime? end = null;
            bool hasTime = dateEnabled.IsChecked == true && timeEnabled.IsChecked == true;
            int reminder = -1;
            if (dateEnabled.IsChecked == true)
            {
                DateTime parsedDate;
                bool parsed = DateTime.TryParse(datePicker.Text, CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.AllowWhiteSpaces, out parsedDate);
                if (!parsed || dateParseFailed || !datePicker.SelectedDate.HasValue)
                {
                    ShowError("请填写有效日期，例如 2026/10/26。", datePicker);
                    return;
                }
                DateTime selected = parsedDate.Date;
                if (hasTime)
                {
                    DateTime parsedTime;
                    if (!DateTime.TryParseExact(timeBox.Text.Trim(), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out parsedTime))
                    {
                        ShowError("时间格式应为 09:30 或 18:00。", timeBox);
                        return;
                    }
                    selected = selected.AddHours(parsedTime.Hour).AddMinutes(parsedTime.Minute);
                }
                due = selected;
                if (hasTime && endEnabled.IsChecked == true)
                {
                    DateTime parsedEndDate;
                    bool endParsed = DateTime.TryParse(endDatePicker.Text, CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.AllowWhiteSpaces, out parsedEndDate);
                    if (!endParsed || endDateParseFailed || !endDatePicker.SelectedDate.HasValue)
                    {
                        ShowError("请填写有效的结束日期。", endDatePicker);
                        return;
                    }
                    DateTime parsedEndTime;
                    if (!DateTime.TryParseExact(endTimeBox.Text.Trim(), new[] { "HH:mm", "H:mm" }, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out parsedEndTime))
                    {
                        ShowError("结束时间格式应为 09:30 或 18:00。", endTimeBox);
                        return;
                    }
                    end = parsedEndDate.Date.AddHours(parsedEndTime.Hour).AddMinutes(parsedEndTime.Minute);
                    if (end.Value <= selected)
                    {
                        ShowError("结束时间必须晚于开始时间；跨夜日程请调整结束日期。", endDatePicker);
                        return;
                    }
                }
                int index = reminderBox.SelectedIndex;
                reminder = index >= 0 && index < reminderValues.Length ? reminderValues[index]
                    : original != null ? original.ReminderMinutes : -1;
            }
            bool scheduleUnchanged = original != null && Nullable.Equals(original.DueAt, due)
                && original.HasTime == hasTime && original.ReminderMinutes == reminder;
            Result = new TaskItem
            {
                Id = original == null ? Guid.NewGuid().ToString("N") : original.Id,
                Title = title,
                Notes = notesBox.Text.Trim(),
                DueAt = due,
                EndAt = end,
                ColorId = colorIds[Math.Max(0, colorBox.SelectedIndex)],
                HasTime = hasTime,
                ReminderMinutes = reminder,
                Completed = original != null && original.Completed,
                NotifiedAt = scheduleUnchanged ? original.NotifiedAt : null
            };
            DialogResult = true;
        }

        private void ShowError(string message, Control focus)
        {
            errorText.Text = message;
            if (focus != null) focus.Focus();
        }

        private TextBlock Label(string text)
        {
            return new TextBlock { Text = text, Foreground = textBrush, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 7) };
        }

        private TextBox MakeTextBox(string name, int maximum)
        {
            var box = new TextBox
            {
                MaxLength = maximum,
                Height = 36,
                Padding = new Thickness(10, 6, 10, 6),
                FontSize = 14,
                Background = inputBrush,
                Foreground = textBrush,
                BorderBrush = lineBrush,
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                SelectionBrush = Brush("#FFD1CB")
            };
            AutomationProperties.SetName(box, name);
            return box;
        }

        private CheckBox MakeCheckBox(string title, bool value)
        {
            var box = new CheckBox { Content = title, IsChecked = value, Foreground = textBrush, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(box, title);
            return box;
        }

        private Button MakeButton(string text, bool primary)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = primary ? 112 : 80,
                Height = 38,
                Padding = new Thickness(14, 7, 14, 7),
                Background = primary ? Brush("#FF3B30") : inputBrush,
                Foreground = primary ? Brushes.White : textBrush,
                BorderBrush = primary ? Brush("#FF3B30") : lineBrush,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                FontWeight = FontWeights.Medium
            };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            button.MouseEnter += delegate { button.Opacity = 0.85; };
            button.MouseLeave += delegate { button.Opacity = 1; };
            AutomationProperties.SetName(button, text);
            return button;
        }

        private static SolidColorBrush Brush(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }
}
