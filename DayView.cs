using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Daynote
{
    public static class CalendarColors
    {
        public static Brush Accent(string id, bool dark)
        {
            string value;
            switch (id)
            {
                case "purple": value = dark ? "#C8A4FF" : "#884DCE"; break;
                case "green": value = dark ? "#83D9A4" : "#27834B"; break;
                case "orange": value = dark ? "#FFC282" : "#AE6714"; break;
                case "teal": value = dark ? "#7ED9D6" : "#15827F"; break;
                case "red": value = dark ? "#FF9A9F" : "#CC4854"; break;
                default: value = dark ? "#89BBFF" : "#286CC7"; break;
            }
            return FrozenBrush(value);
        }

        public static Brush Fill(string id, bool dark)
        {
            string value;
            switch (id)
            {
                case "purple": value = dark ? "#372A4C" : "#F0E7FB"; break;
                case "green": value = dark ? "#213D2E" : "#E6F3E8"; break;
                case "orange": value = dark ? "#453423" : "#FFF0DC"; break;
                case "teal": value = dark ? "#1F3D3C" : "#E0F3F1"; break;
                case "red": value = dark ? "#472A30" : "#FCE7E9"; break;
                default: value = dark ? "#263A54" : "#E5EFFD"; break;
            }
            return FrozenBrush(value);
        }

        private static SolidColorBrush FrozenBrush(string hex)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            brush.Freeze();
            return brush;
        }
    }

    // A day timeline only: the surrounding month, day title and week selector
    // are deliberately owned by MainWindow so this view can preserve its scroll.
    public sealed class DayView : UserControl
    {
        private const double HourHeight = 64;
        private const double Gutter = 54;
        private const double TopPadding = 13;
        private const double BottomPadding = 42;
        private readonly DateTime date;
        private readonly bool dark;
        private readonly List<TaskItem> tasks;
        private readonly List<CalendarSlot> slots;
        private readonly Action<TaskItem> edit;
        private readonly Action<TaskItem> toggle;
        private readonly Action<TaskItem> delete;
        private readonly Action<DateTime> addAt;
        private readonly ScrollViewer scroll;
        private readonly Canvas timeline;
        private readonly Canvas nowMarker;
        private readonly TextBlock nowText;
        private readonly Border nowLine;
        private readonly DispatcherTimer clock;
        private readonly List<FrameworkElement> gridLines = new List<FrameworkElement>();
        private readonly List<EventVisual> events = new List<EventVisual>();
        private double pendingOffset = Double.NaN;
        private bool initiallyScrolled;

        private sealed class EventVisual
        {
            public CalendarSlot Slot;
            public Button Element;
            public TextBlock Title;
            public TextBlock Time;
            public TextBlock Notes;
        }

        public double ScrollOffset
        {
            get { return !Double.IsNaN(pendingOffset) ? pendingOffset : scroll.VerticalOffset; }
            set
            {
                pendingOffset = Math.Max(0, Double.IsNaN(value) ? 0 : value);
                initiallyScrolled = true;
                if (IsLoaded) ApplyPendingOffset();
            }
        }

        public DayView(DateTime date, IEnumerable<TaskItem> tasks, bool dark, bool includeCompleted,
            Action<TaskItem> edit, Action<TaskItem> toggle, Action<DateTime> addAt, Action<TaskItem> delete = null)
        {
            this.date = date.Date;
            this.dark = dark;
            this.tasks = (tasks ?? Enumerable.Empty<TaskItem>()).Where(t => t != null).ToList();
            this.edit = edit;
            this.toggle = toggle;
            this.addAt = addAt;
            this.delete = delete;
            slots = CalendarLogic.LayoutDay(this.tasks, this.date, includeCompleted);
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Resources.Add(typeof(ScrollBar), ScrollbarStyle());

            var root = new Grid { Background = Brush(dark ? "#1C1C1E" : "#FFFFFF") };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Content = root;
            root.Children.Add(BuildAllDay(includeCompleted));

            timeline = new Canvas { Height = TopPadding + 24 * HourHeight + BottomPadding,
                Background = Brushes.Transparent, ClipToBounds = true };
            timeline.MouseLeftButtonUp += OnEmptyTimeClick;
            timeline.ToolTip = "点击空白时段添加日程";
            scroll = new ScrollViewer {
                Content = timeline,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                CanContentScroll = false,
                PanningMode = PanningMode.VerticalOnly,
                Focusable = false
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            BuildHours();
            foreach (var slot in slots)
            {
                var visual = BuildEvent(slot);
                events.Add(visual);
                timeline.Children.Add(visual.Element);
                Panel.SetZIndex(visual.Element, 10 + slot.Column);
            }

            nowMarker = new Canvas { Height = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            nowLine = new Border { Background = Brush("#F04B55"), Height = 1.4 };
            Canvas.SetLeft(nowLine, Gutter - 2); nowMarker.Children.Add(nowLine);
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Brush("#F04B55") };
            Canvas.SetLeft(dot, Gutter - 5); Canvas.SetTop(dot, -2.5); nowMarker.Children.Add(dot);
            nowText = new TextBlock { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var badge = new Border { Background = Brush("#F04B55"), CornerRadius = new CornerRadius(4),
                Width = 40, Height = 18, Child = nowText };
            Canvas.SetLeft(badge, 2); Canvas.SetTop(badge, -8); nowMarker.Children.Add(badge);
            timeline.Children.Add(nowMarker); Panel.SetZIndex(nowMarker, 1000);

            timeline.SizeChanged += delegate { PositionVisuals(); };
            clock = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            clock.Tick += delegate { UpdateNow(); };
            Loaded += delegate {
                PositionVisuals(); UpdateNow(); clock.Start();
                if (!initiallyScrolled) ScrollToRelevantTime();
                else ApplyPendingOffset();
            };
            Unloaded += delegate { clock.Stop(); };
        }

        private UIElement BuildAllDay(bool includeCompleted)
        {
            var shell = new Border { BorderBrush = Brush(dark ? "#3A3D45" : "#E6E9EE"),
                BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 8, 9) };
            var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gutter) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            shell.Child = layout;
            var label = new TextBlock { Text = "全天", FontSize = 11, Foreground = Muted,
                Margin = new Thickness(7, 6, 0, 0) };
            layout.Children.Add(label);
            var allday = CalendarLogic.TasksForDay(tasks, date, includeCompleted).Where(t => !t.HasTime)
                .OrderBy(t => t.Completed).ThenBy(t => t.Title).ToList();
            if (allday.Count == 0)
            {
                var empty = new TextBlock { Text = "暂无全天安排", Foreground = Muted, FontSize = 11,
                    Margin = new Thickness(3, 6, 0, 5) };
                Grid.SetColumn(empty, 1); layout.Children.Add(empty);
            }
            else
            {
                var stack = new StackPanel();
                foreach (var task in allday)
                {
                    var text = new TextBlock { Text = (task.Completed ? "✓  " : "") + task.Title,
                        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                        FontSize = 13, FontWeight = FontWeights.Medium,
                        Foreground = CalendarColors.Accent(task.ColorId, dark) };
                    var chip = StyledButton(task, text, new Thickness(8, 5, 8, 5));
                    chip.Margin = new Thickness(0, 0, 0, 4);
                    chip.MinHeight = 28;
                    stack.Children.Add(chip);
                }
                // Keep the time grid reachable even with many all-day entries.
                var alldayScroll = new ScrollViewer { Content = stack, MaxHeight = 124,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Focusable = false };
                Grid.SetColumn(alldayScroll, 1); layout.Children.Add(alldayScroll);
            }
            return shell;
        }

        private void BuildHours()
        {
            for (int hour = 0; hour <= 24; hour++)
            {
                double top = TopPadding + hour * HourHeight;
                var label = new TextBlock {
                    Text = hour.ToString("00", CultureInfo.InvariantCulture) + ":00",
                    FontSize = 11, Foreground = Muted, Width = 44, TextAlignment = TextAlignment.Right,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(label, 0); Canvas.SetTop(label, top - 8); timeline.Children.Add(label);
                var line = new Border { Height = 1, Background = Brush(dark ? "#373B43" : "#E8EBF0"), IsHitTestVisible = false };
                Canvas.SetLeft(line, Gutter); Canvas.SetTop(line, top); timeline.Children.Add(line); gridLines.Add(line);
                if (hour < 24)
                {
                    var half = new Border { Height = 1, Background = Brush(dark ? "#292D34" : "#F3F4F7"), IsHitTestVisible = false };
                    Canvas.SetLeft(half, Gutter); Canvas.SetTop(half, top + HourHeight / 2); timeline.Children.Add(half); gridLines.Add(half);
                }
            }
        }

        private EventVisual BuildEvent(CalendarSlot slot)
        {
            double height = Math.Max(32, (slot.EndMinute - slot.StartMinute) / 60 * HourHeight);
            bool compact = height < 40;
            var task = slot.Task;
            var stack = new StackPanel { ClipToBounds = true };
            var time = new TextBlock {
                Text = EventTime(slot), Foreground = CalendarColors.Accent(task.ColorId, dark),
                FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                LineHeight = 14, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Visibility = compact ? Visibility.Collapsed : Visibility.Visible
            };
            var title = new TextBlock {
                Text = (task.Completed ? "✓ " : "") + task.Title + (compact ? "  ·  " + EventTime(slot) : ""),
                Foreground = CalendarColors.Accent(task.ColorId, dark), FontSize = 13,
                FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineHeight = 18, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
            if (task.Completed) title.TextDecorations = TextDecorations.Strikethrough;
            // Short blocks keep one readable title/time row rather than tiny
            // stacked type. Tooltips retain the full real interval and notes.
            // Events without EndAt never display a synthetic ending time.
            stack.Children.Add(time); stack.Children.Add(title);
            var notes = new TextBlock {
                Text = task.Notes ?? "", FontSize = 12, Foreground = CalendarColors.Accent(task.ColorId, dark),
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                LineHeight = 18, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Margin = new Thickness(0, 5, 0, 0), Opacity = .85,
                Visibility = height >= 76 && !String.IsNullOrWhiteSpace(task.Notes) ? Visibility.Visible : Visibility.Collapsed,
                MaxHeight = Math.Max(0, height - 43)
            };
            stack.Children.Add(notes);
            var button = StyledButton(task, stack, new Thickness(7, 2, 5, 2));
            button.Height = height;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.VerticalContentAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Top;
            button.ClipToBounds = true;
            Canvas.SetTop(button, TopPadding + slot.StartMinute / 60 * HourHeight);
            return new EventVisual { Slot = slot, Element = button, Time = time, Title = title, Notes = notes };
        }

        private Button StyledButton(TaskItem task, UIElement content, Thickness padding)
        {
            var button = new Button {
                Content = content, Padding = padding, Cursor = Cursors.Hand,
                BorderBrush = CalendarColors.Accent(task.ColorId, dark),
                Background = CalendarColors.Fill(task.ColorId, dark),
                BorderThickness = new Thickness(3, 0, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Opacity = task.Completed ? .58 : 1,
                FocusVisualStyle = null
            };
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.SetValue(Border.ClipToBoundsProperty, true);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            border.AppendChild(presenter); template.VisualTree = border;
            button.Template = template;
            button.Click += delegate { if (edit != null) edit(task); };
            button.MouseEnter += delegate { button.Opacity = task.Completed ? .72 : .86; };
            button.MouseLeave += delegate { button.Opacity = task.Completed ? .58 : 1; };
            button.GotKeyboardFocus += delegate { button.BorderThickness = new Thickness(3, 1, 1, 1); };
            button.LostKeyboardFocus += delegate { button.BorderThickness = new Thickness(3, 0, 0, 0); };

            string description = FullDescription(task);
            button.ToolTip = new ToolTip { Content = new TextBlock { Text = description,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 360, FontSize = 12 } };
            AutomationProperties.SetName(button, description);
            var context = new ContextMenu { FontFamily = FontFamily, FontSize = 13 };
            var editItem = new MenuItem { Header = "编辑日程" }; editItem.Click += delegate { if (edit != null) edit(task); };
            var completeItem = new MenuItem { Header = task.Completed ? "恢复为未完成" : "标记为已完成" };
            completeItem.Click += delegate { if (toggle != null) toggle(task); };
            context.Items.Add(editItem); context.Items.Add(completeItem); button.ContextMenu = context;
            if (delete != null)
            {
                var deleteItem = new MenuItem { Header = "删除事项" };
                deleteItem.Click += delegate { delete(task); };
                context.Items.Add(new Separator()); context.Items.Add(deleteItem);
            }
            return button;
        }

        private string FullDescription(TaskItem task)
        {
            string value = task.Title + "\n";
            if (task.DueAt.HasValue)
            {
                value += task.DueAt.Value.ToString(task.HasTime ? "M月d日 dddd HH:mm" : "M月d日 dddd · 全天", CultureInfo.GetCultureInfo("zh-CN"));
                if (task.HasTime && task.EndAt.HasValue)
                    value += " – " + task.EndAt.Value.ToString(task.EndAt.Value.Date == task.DueAt.Value.Date ? "HH:mm" : "M月d日 HH:mm", CultureInfo.GetCultureInfo("zh-CN"));
            }
            if (!String.IsNullOrWhiteSpace(task.Notes)) value += "\n\n" + task.Notes;
            if (task.Completed) value += "\n已完成";
            return value;
        }

        private string EventTime(CalendarSlot slot)
        {
            TaskItem task = slot.Task;
            if (!task.DueAt.HasValue) return "";
            string start = task.DueAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (slot.ContinuationBefore) start = "← " + task.DueAt.Value.ToString("M/d HH:mm", CultureInfo.InvariantCulture);
            if (!task.EndAt.HasValue) return start;
            string end = task.EndAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (slot.ContinuationAfter) end = task.EndAt.Value.ToString("M/d HH:mm", CultureInfo.InvariantCulture) + " →";
            return start + "–" + end;
        }

        private void PositionVisuals()
        {
            double width = timeline.ActualWidth;
            if (width < Gutter + 20) return;
            double available = Math.Max(20, width - Gutter - 7);
            foreach (var line in gridLines) line.Width = available + 1;
            foreach (var visual in events)
            {
                int columns = Math.Max(1, visual.Slot.ColumnCount);
                double columnWidth = available / columns;
                double eventWidth = Math.Max(1, columnWidth - Math.Min(4, columnWidth * .15));
                Canvas.SetLeft(visual.Element, Gutter + visual.Slot.Column * columnWidth + 1);
                visual.Element.Width = eventWidth;
                // The tooltip retains full details when many overlaps make
                // columns narrow; text never paints over its neighbour.
            }
            nowMarker.Width = width;
            nowLine.Width = available + 3;
        }

        private void OnEmptyTimeClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || addAt == null) return;
            DependencyObject origin = e.OriginalSource as DependencyObject;
            for (var item = origin; item != null && item != timeline; item = VisualTreeHelper.GetParent(item))
                if (item is Button) return;
            Point point = e.GetPosition(timeline);
            if (point.X < Gutter || point.Y < TopPadding || point.Y > TopPadding + 24 * HourHeight) return;
            double minute = (point.Y - TopPadding) / HourHeight * 60;
            int snappedMinute = Math.Max(0, Math.Min(1425, (int)Math.Floor(minute / 15) * 15));
            e.Handled = true;
            addAt(date.AddMinutes(snappedMinute));
        }

        private void UpdateNow()
        {
            DateTime now = DateTime.Now;
            if (date != now.Date) { nowMarker.Visibility = Visibility.Collapsed; return; }
            nowMarker.Visibility = Visibility.Visible;
            nowText.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
            Canvas.SetTop(nowMarker, TopPadding + now.TimeOfDay.TotalMinutes / 60 * HourHeight);
        }

        public void ScrollToRelevantTime()
        {
            double minute = 7 * 60;
            if (slots.Count > 0)
            {
                double first = slots.Min(s => s.StartMinute);
                minute = Math.Max(0, first - 45);
                // An overnight continuation at 00:00 should not force a whole
                // workday view back to midnight when later events are present.
                var later = slots.Where(s => !s.ContinuationBefore && s.StartMinute >= 5 * 60).ToList();
                if (first == 0 && slots.Any(s => s.ContinuationBefore) && later.Count > 0)
                    minute = Math.Max(0, later.Min(s => s.StartMinute) - 45);
            }
            initiallyScrolled = true;
            pendingOffset = Math.Max(0, TopPadding + minute / 60 * HourHeight - 9);
            if (IsLoaded) ApplyPendingOffset();
        }

        private void ApplyPendingOffset()
        {
            if (Double.IsNaN(pendingOffset)) return;
            double desired = pendingOffset;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                scroll.ScrollToVerticalOffset(desired);
                pendingOffset = Double.NaN;
            }));
        }

        private Brush Muted { get { return Brush(dark ? "#9B9FAA" : "#959AA4"); } }
        private static Brush Brush(string value) { return (SolidColorBrush)new BrushConverter().ConvertFromString(value); }

        private Style ScrollbarStyle()
        {
            string thumb = dark ? "#515661" : "#D4D8DF";
            // Track's Thumb/RepeatButton members are CLR properties, so XAML
            // constructs them without using unsupported factory DP setters.
            return (Style)System.Windows.Markup.XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ScrollBar}'>
  <Setter Property='Width' Value='8'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ScrollBar}'>
    <Track x:Name='PART_Track' Orientation='Vertical' IsDirectionReversed='True'
        Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}'
        ViewportSize='{TemplateBinding ViewportSize}' Value='{TemplateBinding Value}'>
      <Track.DecreaseRepeatButton><RepeatButton Command='{x:Static ScrollBar.PageUpCommand}' Focusable='False' IsTabStop='False'>
        <RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
      </RepeatButton></Track.DecreaseRepeatButton>
      <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'>
        <Border Background='" + thumb + @"' CornerRadius='3' Margin='2,2,1,2'/>
      </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
      <Track.IncreaseRepeatButton><RepeatButton Command='{x:Static ScrollBar.PageDownCommand}' Focusable='False' IsTabStop='False'>
        <RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
      </RepeatButton></Track.IncreaseRepeatButton>
    </Track>
  </ControlTemplate></Setter.Value></Setter>
</Style>");
        }
    }
}
