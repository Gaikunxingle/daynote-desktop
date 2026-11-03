using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Daynote
{
    public sealed class CalendarViewState
    {
        public DateTime SelectedDate, VisibleMonth;
        public bool DayMode, IncludeCompleted, Expanded, ShowCompletedDeadlines;
        public double ScrollOffset;
    }
    public sealed class MainWindow : Window
    {
        readonly AppController controller;
        DesktopHost host;
        bool loaded, dayMode, includeCompleted=true;
        bool showCompletedDeadlines;
        double deadlineScrollOffset;
        Grid cardLayout;
        RowDefinition deadlineRow;
        UniformGrid monthCalendar;
        Thumb activeResizeGrip, activeSplitGrip;
        bool adjustingSplit, closing, monthRefreshQueued;
        string layoutGesture="";
        double splitStartHeight, splitStartY, splitStartRatio;
        double? splitPreviewHeight;
        DateTime selectedDate=DateTime.Today, visibleMonth=new DateTime(DateTime.Today.Year,DateTime.Today.Month,1);
        DateTime revealUntil;
        DayView dayView;
        double? restoredScrollOffset;
        bool expanded, expandWhenLoaded, firstClickCanExpand;
        object firstClickContent;
        static readonly DependencyProperty CalendarActionProperty=DependencyProperty.RegisterAttached("CalendarAction",typeof(bool),typeof(MainWindow),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.Inherits));
        DispatcherTimer captureTimer;
        public bool IsDark { get; private set; }
        public DateTime SelectedDate { get { return selectedDate; } }
        public bool IsExpandedView { get { return expanded; } }
        public bool IsLayoutInteractionActive { get { return activeResizeGrip!=null||activeSplitGrip!=null; } }
        bool Compact { get { return !expanded && (controller.Data.Settings.Width < 740||controller.Data.Settings.Height < 650); } }
        double LayoutHeight { get { return expanded&&host!=null?host.ExpandedHeightDip:controller.Data.Settings.Height; } }
        double AutoDeadlineHeight { get { int count=controller.Data.Tasks.Count(t=>t.IsDeadline&&(showCompletedDeadlines||!t.Completed));return 40+(count==0?34:Math.Min(count,expanded?5:2)*44); } }
        double DeadlinePanelHeight { get { return deadlineRow!=null&&deadlineRow.ActualHeight>0?deadlineRow.ActualHeight:AutoDeadlineHeight; } }
        Brush TextColor { get { return Ui.Brush(IsDark?"#F5F5F7":"#1C1C1E"); } }
        Brush Muted { get { return Ui.Brush(IsDark?"#98989F":"#8E8E93"); } }
        Brush Line { get { return Ui.Brush(IsDark?"#38383A":"#E5E5EA"); } }
        Brush Surface { get { return Ui.Brush(IsDark?"#1C1C1E":"#FFFFFF"); } }
        Brush Red { get { return Ui.Brush(IsDark?"#FF453A":"#FF3B30"); } }
        static readonly string[] Months={"一月","二月","三月","四月","五月","六月","七月","八月","九月","十月","十一月","十二月"};
        static readonly string[] Weeks={"日","一","二","三","四","五","六"};

        public MainWindow(AppController controller)
        {
            this.controller=controller;
            Title="朝夕 · 日历";
            Width=controller.Data.Settings.Width; Height=controller.Data.Settings.Height;
            MinWidth=560; MaxWidth=16384; MinHeight=420; MaxHeight=16384;
            WindowStyle=WindowStyle.None; ResizeMode=ResizeMode.NoResize; AllowsTransparency=true; Background=Brushes.Transparent;
            ShowInTaskbar=Program.Preview||Program.Inspect; ShowActivated=Program.Preview||Program.Inspect; Topmost=false;
            FontFamily=new FontFamily("Microsoft YaHei UI"); UseLayoutRounding=true; SnapsToDevicePixels=true;
            var work=SystemParameters.WorkArea;
            Left=controller.Data.Settings.FollowPrimaryTopRight||Double.IsNaN(controller.Data.Settings.Left)?work.Right-Width-16:controller.Data.Settings.Left;
            Top=controller.Data.Settings.FollowPrimaryTopRight||Double.IsNaN(controller.Data.Settings.Top)?work.Top+16:controller.Data.Settings.Top;
            ClampPlacement();
            if(Program.StartDay.HasValue) { selectedDate=Program.StartDay.Value.Date;visibleMonth=new DateTime(selectedDate.Year,selectedDate.Month,1);dayMode=true; }
            Render();
            PreviewMouseLeftButtonDown+=OnCardMouseDown;
            Loaded+=delegate {
                loaded=true;host=new DesktopHost(this);
                ApplyPlacement();
                if(controller.Data.Settings.DesktopMode&&!Program.Preview)host.Attach();
                ApplyPlacement();
                if(expandWhenLoaded) { expandWhenLoaded=false;ToggleExpandedView(); }
                WriteStatus();ScheduleCapture();
                var placementTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(400)};
                placementTimer.Tick+=delegate{placementTimer.Stop();if(IsLoaded)SavePlacement();};placementTimer.Start();
            };
            Closing+=delegate { closing=true;if(activeResizeGrip!=null)activeResizeGrip.CancelDrag();if(activeSplitGrip!=null)activeSplitGrip.CancelDrag(); };
            Closed+=delegate { if(captureTimer!=null)captureTimer.Stop();if(host!=null)host.Dispose(); };
            PreviewKeyDown+=delegate(object sender,KeyEventArgs e) { if(e.Key==Key.Escape&&IsLayoutInteractionActive) { if(activeResizeGrip!=null)activeResizeGrip.CancelDrag();if(activeSplitGrip!=null)activeSplitGrip.CancelDrag();e.Handled=true; } };
            KeyDown+=delegate(object sender,KeyEventArgs e) {
                if(e.Key==Key.N&&(Keyboard.Modifiers&ModifierKeys.Control)!=0) { controller.Edit(null,selectedDate);e.Handled=true; }
                else if(e.Key==Key.Escape&&expanded) { ToggleExpandedView();e.Handled=true; }
                else if(e.Key==Key.Escape&&dayMode) { ShowMonth(selectedDate);e.Handled=true; }
            };
        }
        void ClampPlacement()
        {
            var work=SystemParameters.WorkArea;
            Width=Math.Min(Width,Math.Max(MinWidth,work.Width-36));Height=Math.Min(Height,Math.Max(MinHeight,work.Height-36));
            var area=new Rect(SystemParameters.VirtualScreenLeft,SystemParameters.VirtualScreenTop,SystemParameters.VirtualScreenWidth,SystemParameters.VirtualScreenHeight);
            Left=Math.Max(area.Left,Math.Min(Left,area.Right-Width));Top=Math.Max(area.Top,Math.Min(Top,area.Bottom-Height-40));
        }
        public void SavePlacement()
        {
            if(!loaded||expanded||expandWhenLoaded||IsLayoutInteractionActive)return;
            if(!controller.Data.Settings.FollowPrimaryTopRight) {
                var p=host==null?new Point(Left,Top):host.GetScreenPosition();
                controller.Data.Settings.Left=p.X;controller.Data.Settings.Top=p.Y;
            }
            // Width and Height in settings are the user's canonical size, never
            // a transient WPF nudge or a size coerced by a different monitor DPI.
            controller.Save(false);
        }
        void ApplyPlacement()
        {
            if(host==null||IsLayoutInteractionActive)return;
            if(expanded) { host.MaintainExpanded();return; }
            if(controller.Data.Settings.FollowPrimaryTopRight)host.PlaceOnPrimary(controller.Data.Settings.Width,controller.Data.Settings.Height,16);
            else { host.StopFollowingPrimary();host.EnsureVisible(); }
        }
        public void MaintainDesktop()
        {
            if(host==null||IsLayoutInteractionActive)return;
            if(expanded) { host.MaintainExpanded();WriteStatus();return; }
            if(controller.Data.Settings.DesktopMode&&!Program.Preview&&DateTime.Now>revealUntil) { if(!host.IsAttached)host.Attach();host.Maintain();if(host.NeedsWindowRecreation) { Close();return; } }
            if(DateTime.Now>revealUntil)ApplyPlacement();
            WriteStatus();
        }
        void WriteStatus()
        {
            try { var p=host==null?new Point(Left,Top):host.GetScreenPosition();File.WriteAllText(Path.Combine(controller.Store.Root,"desktop-status.txt"),"version=2.4.1\ndataFile="+controller.Store.FilePath+"\nprocessId="+System.Diagnostics.Process.GetCurrentProcess().Id+"\ntaskCount="+controller.Data.Tasks.Count+"\ndeadlineCount="+controller.Data.Tasks.Count(t=>t.IsDeadline)+"\ntime="+DateTime.Now.ToString("s")+"\nattached="+(host!=null&&host.IsAttached)+"\nmode="+controller.Data.Settings.DesktopMode+"\nerror="+(host==null?"":host.LastError)+"\nleft="+p.X+"\ntop="+p.Y+"\nwidth="+Width+"\nheight="+Height+"\nrequested="+controller.Data.Settings.Width+"x"+controller.Data.Settings.Height+"\nfollowPrimary="+controller.Data.Settings.FollowPrimaryTopRight+"\nexpanded="+expanded+"\nlayoutGesture="+layoutGesture+"\nselectedDate="+selectedDate.ToString("yyyy-MM-dd")+"\ndeadlineRatio="+controller.Data.Settings.DeadlinePanelRatio.ToString(CultureInfo.InvariantCulture)+"\ndeadlineHeight="+DeadlinePanelHeight.ToString(CultureInfo.InvariantCulture)+"\nview="+(dayMode?"day":"month")+"\n"+(host==null?"":host.PlacementDiagnostics())); } catch { }
        }
        public void Reveal() { if(expanded) { host.MaintainExpanded();if(!IsVisible)Show();Activate();return; }if(host!=null)host.Detach();ApplyPlacement();revealUntil=DateTime.Now.AddSeconds(20);if(!IsVisible)Show();WindowState=WindowState.Normal;Activate(); }
        void Drag(object sender,MouseButtonEventArgs e)
        {
            if(e.ChangedButton!=MouseButton.Left)return;
            if(expanded)return;
            if(controller.Data.Settings.FollowPrimaryTopRight)return;
            try { if(host!=null)host.Detach();DragMove();SavePlacement(); } catch(InvalidOperationException) { }
            finally { if(host!=null&&controller.Data.Settings.DesktopMode&&!Program.Preview)host.Attach(); }
        }
        public void ShowMonth(DateTime date) { dayMode=false;visibleMonth=new DateTime(date.Year,date.Month,1);selectedDate=date.Date;Render(); }
        public void ShowDay(DateTime date) { dayMode=true;selectedDate=date.Date;visibleMonth=new DateTime(date.Year,date.Month,1);dayView=null;Render(); }
        public CalendarViewState CaptureViewState() { return new CalendarViewState { SelectedDate=selectedDate,VisibleMonth=visibleMonth,DayMode=dayMode,IncludeCompleted=includeCompleted,ScrollOffset=dayView==null?0:dayView.ScrollOffset,Expanded=expanded||expandWhenLoaded,ShowCompletedDeadlines=showCompletedDeadlines }; }
        public void RestoreViewState(CalendarViewState state) { selectedDate=state.SelectedDate;visibleMonth=state.VisibleMonth;dayMode=state.DayMode;includeCompleted=state.IncludeCompleted;showCompletedDeadlines=state.ShowCompletedDeadlines;dayView=null;restoredScrollOffset=state.DayMode?(double?)state.ScrollOffset:null;expandWhenLoaded=state.Expanded;Render(); }
        bool IsExpansionSurface(DependencyObject source)
        {
            for(var current=source;current!=null;)
            {
                if(current is ButtonBase || current is MenuItem || current is TextBoxBase || current is ScrollViewer || current is ScrollBar || current is Thumb || current is ComboBox || (bool)current.GetValue(CalendarActionProperty))return false;
                if(Object.ReferenceEquals(current,this))return true;
                var content=current as FrameworkContentElement;
                if(content!=null)current=content.Parent??ContentOperations.GetParent(content);
                else if(current is Visual)current=VisualTreeHelper.GetParent(current);
                else current=LogicalTreeHelper.GetParent(current);
            }
            return false;
        }
        void OnCardMouseDown(object sender,MouseButtonEventArgs e)
        {
            layoutGesture="down:"+e.OriginalSource.GetType().Name;
            if(e.ChangedButton!=MouseButton.Left)return;
            bool eligible=IsExpansionSurface(e.OriginalSource as DependencyObject);
            if(e.ClickCount==1) { firstClickCanExpand=eligible;firstClickContent=Content;return; }
            bool toggle=e.ClickCount==2&&eligible&&firstClickCanExpand&&Object.ReferenceEquals(firstClickContent,Content);
            firstClickCanExpand=false;firstClickContent=null;
            if(toggle) { e.Handled=true;ToggleExpandedView(); }
        }
        public void ToggleExpandedView()
        {
            if(host==null||IsLayoutInteractionActive)return;
            firstClickCanExpand=false;firstClickContent=null;
            if(!expanded) {
                SavePlacement();
                if(!host.EnterExpanded())return;
                expanded=true;
            } else {
                host.ExitExpanded();expanded=false;revealUntil=DateTime.MinValue;
                if(controller.Data.Settings.DesktopMode&&!Program.Preview)host.Attach();else host.Detach();
                ApplyPlacement();
            }
            Render();WriteStatus();
        }
        public void Render()
        {
            if(closing||IsLayoutInteractionActive)return;
            IsDark=controller.Dark;
            double? offset=restoredScrollOffset ?? (dayMode&&dayView!=null?(double?)dayView.ScrollOffset:null);restoredScrollOffset=null;
            var outer=new Border { CornerRadius=new CornerRadius(expanded?0:20),Margin=new Thickness(expanded?0:6),Background=Surface,BorderBrush=Ui.Brush(IsDark?"#48484A":"#D1D1D6"),BorderThickness=new Thickness(1),Padding=new Thickness(0),Opacity=controller.Data.Settings.Opacity };
            var layout=new Grid();cardLayout=layout;monthCalendar=null;
            layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(10)});deadlineRow=new RowDefinition{Height=new GridLength(AutoDeadlineHeight)};layout.RowDefinitions.Add(deadlineRow);layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});outer.Child=layout;
            var head=new StackPanel { Margin=Compact?new Thickness(16,12,16,0):new Thickness(24,18,24,0) };layout.Children.Add(head);
            head.Children.Add(Toolbar());
            if(dayMode) {
                head.Children.Add(WeekStrip());
                var date=Ui.Text(selectedDate.ToString("yyyy年M月d日 · dddd",CultureInfo.GetCultureInfo("zh-CN")),Compact?15:18,TextColor,FontWeights.SemiBold);date.HorizontalAlignment=HorizontalAlignment.Center;date.Margin=new Thickness(0,Compact?9:15,0,4);date.MouseLeftButtonDown+=Drag;date.ToolTip=PositionTip();head.Children.Add(date);
                var lunar=Ui.Text(CalendarLogic.LunarLong(selectedDate),Compact?10:12,Muted,FontWeights.Normal);lunar.HorizontalAlignment=HorizontalAlignment.Center;lunar.Margin=new Thickness(0,0,0,Compact?9:16);head.Children.Add(lunar);
                dayView=new DayView(selectedDate,controller.Data.Tasks,IsDark,includeCompleted,delegate(TaskItem t){controller.Edit(t);},controller.Toggle,delegate(DateTime d){controller.Edit(null,d);},controller.Delete);
                if(offset.HasValue)dayView.ScrollOffset=offset.Value;
                Grid.SetRow(dayView,1);layout.Children.Add(dayView);
            } else {
                dayView=null;
                if(!Compact) { var month=Ui.Text(Months[visibleMonth.Month-1],35,TextColor,FontWeights.Bold);month.Margin=new Thickness(0,17,0,17);month.MouseLeftButtonDown+=Drag;head.Children.Add(month); }
                var weekdays=new UniformGrid{Columns=7,Margin=Compact?new Thickness(-4,10,-4,8):new Thickness(-12,0,-12,10)};
                for(int i=0;i<7;i++) { var item=Ui.Text(Weeks[i],13,i==0||i==6?Muted:TextColor,FontWeights.Medium);item.HorizontalAlignment=HorizontalAlignment.Center;weekdays.Children.Add(item); }head.Children.Add(weekdays);
                var calendar=MonthGrid();Grid.SetRow(calendar,1);layout.Children.Add(calendar);
            }
            var splitter=SplitGrip();Grid.SetRow(splitter,2);layout.Children.Add(splitter);
            var deadlines=DeadlinePanel();Grid.SetRow(deadlines,3);layout.Children.Add(deadlines);
            var footer=Footer();Grid.SetRow(footer,4);layout.Children.Add(footer);
            layout.SizeChanged+=delegate{if(Object.ReferenceEquals(cardLayout,layout))UpdateSplitLayout();};
            layout.Loaded+=delegate{if(Object.ReferenceEquals(cardLayout,layout))UpdateSplitLayout();};
            var root=new Grid();root.Children.Add(outer);if(!expanded)AddResizeGrips(root);Content=root;
            if(loaded) { WriteStatus();ScheduleCapture(); }
        }
        double AvailablePanelHeight()
        {
            if(cardLayout==null||cardLayout.ActualHeight<=0)return 0;
            return Math.Max(0,cardLayout.ActualHeight-cardLayout.RowDefinitions[0].ActualHeight-cardLayout.RowDefinitions[4].ActualHeight-10);
        }
        double TopPanelMinimum { get { return dayMode?105:CalendarLogic.MonthRowCount(visibleMonth)*(Compact?34:60); } }
        void UpdateSplitLayout()
        {
            if(adjustingSplit||cardLayout==null||deadlineRow==null)return;
            double available=AvailablePanelHeight();if(available<=0)return;
            adjustingSplit=true;
            try {
                double ratio=splitPreviewHeight.HasValue?splitPreviewHeight.Value/available:controller.Data.Settings.DeadlinePanelRatio;
                double height=PanelLayout.DeadlineHeight(available,ratio,AutoDeadlineHeight,TopPanelMinimum,74);
                if(Math.Abs(deadlineRow.Height.Value-height)>.1)deadlineRow.Height=new GridLength(height);
            } finally { adjustingSplit=false; }
        }
        void QueueMonthRefresh()
        {
            if(monthCalendar==null||monthRefreshQueued||closing||IsLayoutInteractionActive)return;
            monthRefreshQueued=true;var calendar=monthCalendar;
            Dispatcher.BeginInvoke(new Action(delegate {
                monthRefreshQueued=false;if(closing||IsLayoutInteractionActive)return;
                if(!Object.ReferenceEquals(calendar,monthCalendar)) { QueueMonthRefresh();return; }
                int rows=CalendarLogic.MonthRowCount(visibleMonth);DateTime start=CalendarLogic.MonthGridStart(visibleMonth);
                calendar.Children.Clear();for(int n=0;n<rows*7;n++)calendar.Children.Add(DayCell(start.AddDays(n),rows,n%7));
            }),DispatcherPriority.Background);
        }
        Thumb SplitGrip()
        {
            var grip=new Thumb { Height=10,Cursor=Cursors.SizeNS,Background=Brushes.Transparent,ToolTip="上下拖动，调整日程与截止待办的空间；按 Esc 取消本次拖动",Focusable=true };
            AutomationProperties.SetName(grip,"调整日程和截止待办分界线");
            var template=new ControlTemplate(typeof(Thumb));var background=new FrameworkElementFactory(typeof(Grid));background.SetValue(Panel.BackgroundProperty,Brushes.Transparent);
            var line=new FrameworkElementFactory(typeof(Border));line.SetValue(Border.HeightProperty,1.0);line.SetValue(Border.VerticalAlignmentProperty,VerticalAlignment.Center);line.SetValue(Border.MarginProperty,new Thickness(16,0,16,0));line.SetValue(Border.BackgroundProperty,Line);background.AppendChild(line);
            var handle=new FrameworkElementFactory(typeof(Border));handle.SetValue(Border.WidthProperty,34.0);handle.SetValue(Border.HeightProperty,3.0);handle.SetValue(Border.CornerRadiusProperty,new CornerRadius(2));handle.SetValue(Border.VerticalAlignmentProperty,VerticalAlignment.Center);handle.SetValue(Border.HorizontalAlignmentProperty,HorizontalAlignment.Center);handle.SetValue(Border.BackgroundProperty,Muted);background.AppendChild(handle);template.VisualTree=background;grip.Template=template;
            grip.DragStarted+=delegate {
                if(activeResizeGrip!=null||AvailablePanelHeight()<=0) { grip.CancelDrag();return; }
                activeSplitGrip=grip;splitStartHeight=deadlineRow.ActualHeight;splitStartY=Mouse.GetPosition(cardLayout).Y;splitStartRatio=controller.Data.Settings.DeadlinePanelRatio;grip.Focus();
            };
            grip.DragDelta+=delegate {
                if(activeSplitGrip!=grip)return;
                double available=AvailablePanelHeight();if(available<=0)return;
                double requested=splitStartHeight-(Mouse.GetPosition(cardLayout).Y-splitStartY);
                double ratio=Math.Max(.000001,Math.Min(1,requested/available));
                splitPreviewHeight=PanelLayout.DeadlineHeight(available,ratio,AutoDeadlineHeight,TopPanelMinimum,74);UpdateSplitLayout();
            };
            grip.DragCompleted+=delegate(object sender,DragCompletedEventArgs e) {
                if(activeSplitGrip!=grip)return;
                double available=AvailablePanelHeight();
                controller.Data.Settings.DeadlinePanelRatio=e.Canceled?splitStartRatio:available>0?Math.Max(.000001,Math.Min(1,deadlineRow.Height.Value/available)):splitStartRatio;
                activeSplitGrip=null;splitPreviewHeight=null;
                if(!e.Canceled&&!controller.Save(true))controller.Data.Settings.DeadlinePanelRatio=splitStartRatio;
                Render();
            };
            return grip;
        }
        void AddResizeGrips(Grid root)
        {
            AddResizeGrip(root,-1,0,Cursors.SizeWE,"拖动左边缘调整宽度",10,Double.NaN,HorizontalAlignment.Left,VerticalAlignment.Stretch,new Thickness(0,18,0,18));
            AddResizeGrip(root,1,0,Cursors.SizeWE,"拖动右边缘调整宽度",10,Double.NaN,HorizontalAlignment.Right,VerticalAlignment.Stretch,new Thickness(0,18,0,18));
            AddResizeGrip(root,0,-1,Cursors.SizeNS,"拖动上边缘调整高度",Double.NaN,10,HorizontalAlignment.Stretch,VerticalAlignment.Top,new Thickness(18,0,18,0));
            AddResizeGrip(root,0,1,Cursors.SizeNS,"拖动下边缘调整高度",Double.NaN,10,HorizontalAlignment.Stretch,VerticalAlignment.Bottom,new Thickness(18,0,18,0));
            AddResizeGrip(root,-1,-1,Cursors.SizeNWSE,"拖动左上角调整大小",18,18,HorizontalAlignment.Left,VerticalAlignment.Top,new Thickness(0));
            AddResizeGrip(root,1,-1,Cursors.SizeNESW,"拖动右上角调整大小",18,18,HorizontalAlignment.Right,VerticalAlignment.Top,new Thickness(0));
            AddResizeGrip(root,-1,1,Cursors.SizeNESW,"拖动左下角调整大小",18,18,HorizontalAlignment.Left,VerticalAlignment.Bottom,new Thickness(0));
            AddResizeGrip(root,1,1,Cursors.SizeNWSE,"拖动右下角调整大小",18,18,HorizontalAlignment.Right,VerticalAlignment.Bottom,new Thickness(0));
        }
        void AddResizeGrip(Grid root,int horizontal,int vertical,Cursor cursor,string name,double width,double height,HorizontalAlignment ha,VerticalAlignment va,Thickness margin)
        {
            var grip=new Thumb { Width=width,Height=height,HorizontalAlignment=ha,VerticalAlignment=va,Margin=margin,Cursor=cursor,Background=Ui.Brush("#01000000"),Focusable=true,ToolTip=name+"；按 Esc 取消本次拖动" };
            AutomationProperties.SetName(grip,name);var template=new ControlTemplate(typeof(Thumb));var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));template.VisualTree=border;grip.Template=template;
            grip.DragStarted+=delegate(object sender,DragStartedEventArgs e) {
                layoutGesture="resize-start:"+horizontal+","+vertical;
                Point origin=grip.PointToScreen(new Point(e.HorizontalOffset,e.VerticalOffset));
                if(host==null||expanded||activeSplitGrip!=null||!host.BeginResize(horizontal,vertical,origin)) { layoutGesture+=" rejected";WriteStatus();grip.CancelDrag();return; }
                activeResizeGrip=grip;grip.Focus();WriteStatus();
            };
            grip.DragDelta+=delegate { if(activeResizeGrip==grip) { host.UpdateResize();UpdateSplitLayout(); } };
            grip.DragCompleted+=delegate(object sender,DragCompletedEventArgs e) {
                if(activeResizeGrip!=grip)return;
                DisplayPlacement size=host.EndResize(e.Canceled);activeResizeGrip=null;layoutGesture="resize-end canceled="+e.Canceled+" result="+(size==null?"null":size.WidthDip+"x"+size.HeightDip);
                if(!e.Canceled&&size!=null) { controller.Data.Settings.Width=Math.Max(560,Math.Min(16384,size.WidthDip));controller.Data.Settings.Height=Math.Max(420,Math.Min(16384,size.HeightDip)); }
                ApplyPlacement();if(!e.Canceled)SavePlacement();Render();
            };
            root.Children.Add(grip);
        }
        UIElement Toolbar()
        {
            var bar=new DockPanel { LastChildFill=true };
            var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(actions,Dock.Right);bar.Children.Add(actions);
            actions.Children.Add(ToolButton("‹",delegate{Move(-1);},"上一"+(dayMode?"天":"个月"),28));
            actions.Children.Add(ToolButton("今天",GoToday,"回到今天",15));
            actions.Children.Add(ToolButton("›",delegate{Move(1);},"下一"+(dayMode?"天":"个月"),28));
            var plus=ToolButton("＋",delegate{controller.Edit(null,selectedDate);},"添加事项",27);plus.Margin=new Thickness(8,0,5,0);actions.Children.Add(plus);
            actions.Children.Add(ToolButton("•••",OpenMenu,"设置与更多",16));
            if(dayMode) { var back=ToolButton("‹  "+Months[selectedDate.Month-1],delegate{ShowMonth(selectedDate);},"返回月历",16);back.HorizontalAlignment=HorizontalAlignment.Left;bar.Children.Add(back); }
            else { var year=Ui.Text(Compact?visibleMonth.ToString("yyyy年M月"):visibleMonth.Year+"年",Compact?16:17,TextColor,FontWeights.Medium);year.VerticalAlignment=VerticalAlignment.Center;year.MouseLeftButtonDown+=Drag;year.ToolTip=PositionTip();bar.Children.Add(year); }
            return bar;
        }
        Button ToolButton(string label,Action action,string name,double size)
        {
            var button=Ui.Button(label,action,false,IsDark);button.Background=Brushes.Transparent;button.Foreground=Red;button.FontSize=size;button.Padding=new Thickness(10,4,10,4);button.MinHeight=34;button.ToolTip=name;AutomationProperties.SetName(button,name);return button;
        }
        void Move(int amount)
        {
            try { if(dayMode)ShowDay(selectedDate.AddDays(amount));else ShowMonth(visibleMonth.AddMonths(amount)); } catch(ArgumentOutOfRangeException) { }
        }
        void GoToday() { if(dayMode)ShowDay(DateTime.Today);else ShowMonth(DateTime.Today); }
        UIElement MonthGrid()
        {
            int rows=CalendarLogic.MonthRowCount(visibleMonth);DateTime start=CalendarLogic.MonthGridStart(visibleMonth);
            var calendar=new UniformGrid{Columns=7,Rows=rows,Margin=new Thickness(12,0,12,0)};
            monthCalendar=calendar;
            calendar.SizeChanged+=delegate{QueueMonthRefresh();};
            for(int n=0;n<rows*7;n++) calendar.Children.Add(DayCell(start.AddDays(n),rows,n%7));
            return calendar;
        }
        UIElement DayCell(DateTime date,int rows,int column)
        {
            bool inMonth=date.Month==visibleMonth.Month;bool today=date==DateTime.Today;
            var cell=new Border{BorderBrush=Line,BorderThickness=new Thickness(0,1,column==6?0:1,0),Padding=Compact?new Thickness(4,3,4,2):new Thickness(6,8,6,5),Background=Brushes.Transparent,Cursor=Cursors.Hand,ClipToBounds=true};
            cell.SetValue(CalendarActionProperty,true);
            var body=new StackPanel();var cellGrid=new Grid();cell.Child=cellGrid;cellGrid.Children.Add(body);
            var dateStack=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center};
            var compactHeader=new Grid{Margin=new Thickness(0,0,0,2)};
            compactHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});compactHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});compactHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            var num=Ui.Text(date.Day.ToString(),Compact?17:21,today?Brushes.White:!inMonth?Muted:column==0||column==6?Muted:TextColor,FontWeights.Medium);num.TextAlignment=TextAlignment.Center;num.VerticalAlignment=VerticalAlignment.Center;
            var circle=new Border{Width=Compact?27:34,Height=Compact?27:34,CornerRadius=new CornerRadius(17),Background=today?Red:Brushes.Transparent,Child=num};
            var lunar=Ui.Text(CalendarLogic.LunarShort(date),Compact?9:10,Muted,FontWeights.Normal);lunar.VerticalAlignment=VerticalAlignment.Center;lunar.Margin=Compact?new Thickness(1,0,1,0):new Thickness(0,1,0,5);lunar.ToolTip=CalendarLogic.LunarLong(date);
            if(Compact) { compactHeader.Children.Add(circle);Grid.SetColumn(lunar,1);lunar.TextWrapping=TextWrapping.NoWrap;lunar.TextTrimming=TextTrimming.CharacterEllipsis;compactHeader.Children.Add(lunar);body.Children.Add(compactHeader); }
            else { lunar.HorizontalAlignment=HorizontalAlignment.Center;dateStack.Children.Add(circle);dateStack.Children.Add(lunar);body.Children.Add(dateStack); }
            var tasks=CalendarLogic.TasksForDay(controller.Data.Tasks,date,includeCompleted);
            double gridHeight=monthCalendar!=null&&monthCalendar.ActualHeight>0?monthCalendar.ActualHeight:LayoutHeight-DeadlinePanelHeight-(Compact?162:226);
            int capacity=Math.Max(0,Math.Min(5,(int)((gridHeight/rows-(Compact?34:60))/(Compact?19:21))));
            int count=Math.Min(capacity,tasks.Count);
            for(int i=0;i<count;i++) {
                var task=tasks[i];
                var title=Ui.Text((task.Completed?"✓ ":"")+task.Title,Compact?10.5:11.5,CalendarColors.Accent(task.ColorId,IsDark),FontWeights.Medium);title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;
                if(task.Completed)title.TextDecorations=TextDecorations.Strikethrough;
                var pill=new Border{Background=CalendarColors.Fill(task.ColorId,IsDark),BorderBrush=CalendarColors.Accent(task.ColorId,IsDark),BorderThickness=new Thickness(3,0,0,0),CornerRadius=new CornerRadius(3),Padding=Compact?new Thickness(3,1,2,1):new Thickness(4,2,3,2),Margin=new Thickness(0,1,0,1),Child=title,Opacity=task.Completed?.55:1};
                pill.ToolTip=task.Title+"\n"+ReminderEngine.FormatDue(task)+(task.EndAt.HasValue?" – "+task.EndAt.Value.ToString("M/d HH:mm"):"")+(String.IsNullOrEmpty(task.Notes)?"":"\n"+task.Notes);body.Children.Add(pill);
            }
            if(tasks.Count>count) { var more=Ui.Text("+"+(tasks.Count-count),10,Muted,FontWeights.Medium);more.HorizontalAlignment=HorizontalAlignment.Right;more.VerticalAlignment=Compact?VerticalAlignment.Center:VerticalAlignment.Top;more.Margin=Compact?new Thickness(1,0,0,0):new Thickness(0,4,1,0);more.ToolTip="还有 "+(tasks.Count-count)+" 项，点击日期查看全部";if(Compact){Grid.SetColumn(more,2);compactHeader.Children.Add(more);}else cellGrid.Children.Add(more); }
            if(!inMonth)body.Opacity=.48;
            string label=date.ToString("yyyy年M月d日")+(today?" 今天":"")+"，"+tasks.Count+"项事项";
            AutomationProperties.SetName(cell,label);cell.ToolTip=label+"；点击查看当天日程";
            cell.MouseLeftButtonUp+=delegate { ShowDay(date); };
            cell.MouseEnter+=delegate {cell.Background=Ui.Brush(IsDark?"#262628":"#F8F8FA");};cell.MouseLeave+=delegate {cell.Background=Brushes.Transparent;};
            var context=new ContextMenu();var add=new MenuItem{Header="在 "+date.ToString("M月d日")+" 添加事项"};add.Click+=delegate{controller.Edit(null,date);};context.Items.Add(add);cell.ContextMenu=context;
            return cell;
        }
        UIElement WeekStrip()
        {
            DateTime start=selectedDate.AddDays(-(int)selectedDate.DayOfWeek);
            var week=new UniformGrid{Columns=7,Margin=new Thickness(-8,Compact?7:13,-8,0)};
            for(int i=0;i<7;i++) {
                DateTime date=start.AddDays(i);bool selected=date==selectedDate;bool today=selected&&date==DateTime.Today;
                var inner=new StackPanel();var label=Ui.Text(Weeks[i],11,Muted,FontWeights.Normal);label.HorizontalAlignment=HorizontalAlignment.Center;label.Margin=new Thickness(0,0,0,7);inner.Children.Add(label);
                var circleBody=new StackPanel{VerticalAlignment=VerticalAlignment.Center};Brush fg=today?Brushes.White:selected?Ui.Brush(IsDark?"#1C1C1E":"#FFFFFF"):i==0||i==6?Muted:TextColor;
                var num=Ui.Text(date.Day.ToString(),19,fg,FontWeights.Medium);num.HorizontalAlignment=HorizontalAlignment.Center;circleBody.Children.Add(num);
                var lunar=Ui.Text(CalendarLogic.LunarShort(date),9,fg,FontWeights.Normal);lunar.HorizontalAlignment=HorizontalAlignment.Center;circleBody.Children.Add(lunar);
                var circle=new Border{CornerRadius=new CornerRadius(24),Width=Compact?42:48,Height=Compact?42:48,HorizontalAlignment=HorizontalAlignment.Center,Background=today?Red:selected?Ui.Brush(IsDark?"#F2F2F7":"#1C1C1E"):Brushes.Transparent,Child=circleBody};inner.Children.Add(circle);
                var button=Ui.Button("",delegate{ShowDay(date);},false,IsDark);button.Content=inner;button.Background=Brushes.Transparent;button.Padding=new Thickness(0);button.Margin=new Thickness(0);AutomationProperties.SetName(button,"查看 "+date.ToString("M月d日"));week.Children.Add(button);
            }
            return week;
        }
        UIElement DeadlinePanel()
        {
            var section=new Border { Margin=new Thickness(16,0,16,0),Padding=new Thickness(0,2,0,0),ClipToBounds=true };
            var layout=new Grid();layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(30)});layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});section.Child=layout;
            var header=new DockPanel { LastChildFill=true };
            var actions=new StackPanel { Orientation=Orientation.Horizontal };DockPanel.SetDock(actions,Dock.Right);header.Children.Add(actions);
            int completed=controller.Data.Tasks.Count(t=>t.IsDeadline&&t.Completed);
            if(completed>0) {
                var history=ToolButton((showCompletedDeadlines?"收起已完成":"已完成")+" "+completed,delegate{showCompletedDeadlines=!showCompletedDeadlines;deadlineScrollOffset=0;Render();},"显示或收起已完成的截止待办",10.5);
                history.Foreground=Muted;history.MinHeight=26;history.Padding=new Thickness(6,2,6,2);actions.Children.Add(history);
            }
            var add=ToolButton("＋ 添加",delegate{controller.Edit(null,DateTime.Today,true);},"添加截止待办",12);add.MinHeight=26;add.Padding=new Thickness(5,2,0,2);actions.Children.Add(add);
            int remaining=controller.Data.Tasks.Count(t=>t.IsDeadline&&!t.Completed);
            var heading=Ui.Text("截止待办"+(remaining>0?"  "+remaining:""),13,TextColor,FontWeights.SemiBold);heading.VerticalAlignment=VerticalAlignment.Center;heading.ToolTip="完成前每天显示，不受上方选中日期影响";header.Children.Add(heading);layout.Children.Add(header);
            var tasks=DeadlineLogic.Items(controller.Data.Tasks,showCompletedDeadlines);
            if(tasks.Count==0) {
                var empty=Ui.Text("添加有截止日期的事项，完成前每天可见",11,Muted,FontWeights.Normal);empty.VerticalAlignment=VerticalAlignment.Center;Grid.SetRow(empty,1);layout.Children.Add(empty);
            } else {
                var list=new StackPanel();foreach(var task in tasks)list.Children.Add(DeadlineRow(task));
                var scroll=new ScrollViewer { Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
                scroll.Loaded+=delegate{scroll.ScrollToVerticalOffset(deadlineScrollOffset);};
                scroll.ScrollChanged+=delegate{if(scroll.IsLoaded)deadlineScrollOffset=scroll.VerticalOffset;};
                Grid.SetRow(scroll,1);layout.Children.Add(scroll);
            }
            return section;
        }
        UIElement DeadlineRow(TaskItem task)
        {
            var row=new Border { Height=44,CornerRadius=new CornerRadius(6),Padding=new Thickness(1,2,7,2),Cursor=Cursors.Hand,Background=Brushes.Transparent,Opacity=task.Completed?.55:1 };
            row.SetValue(CalendarActionProperty,true);
            var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(30)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.Child=grid;
            var toggle=ToolButton(task.Completed?"✓":"○",delegate{controller.Toggle(task);},(task.Completed?"恢复截止待办：":"完成截止待办：")+task.Title,23);toggle.Foreground=task.Completed?Muted:CalendarColors.Accent(task.ColorId,IsDark);toggle.Padding=new Thickness(0);toggle.Margin=new Thickness(0,0,5,0);toggle.MinHeight=30;grid.Children.Add(toggle);
            var text=new StackPanel { VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0) };Grid.SetColumn(text,1);grid.Children.Add(text);
            var title=Ui.Text(task.Title,12,TextColor,FontWeights.Medium);title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;if(task.Completed)title.TextDecorations=TextDecorations.Strikethrough;text.Children.Add(title);
            DateTime now=DateTime.Now;
            bool overdue=DeadlineLogic.IsOverdue(task,now);
            string due=task.DueAt.HasValue?task.DueAt.Value.ToString(task.DueAt.Value.Year==now.Year?"M月d日 ddd":"yyyy年M月d日 ddd",CultureInfo.GetCultureInfo("zh-CN"))+(task.HasTime?" "+task.DueAt.Value.ToString("HH:mm"):"")+" 截止":"未设置截止日期";
            var deadline=Ui.Text(due,10.5,overdue?Red:Muted,FontWeights.Normal);deadline.TextWrapping=TextWrapping.NoWrap;deadline.TextTrimming=TextTrimming.CharacterEllipsis;text.Children.Add(deadline);
            string status=task.Completed?"已完成":"待完成";
            if(task.DueAt.HasValue&&!task.Completed) {
                int days=(task.DueAt.Value.Date-now.Date).Days;
                status=overdue?(days<0?"逾期 "+(-days)+" 天":"已逾期"):days==0?"今天截止":days==1?"明天截止":"还剩 "+days+" 天";
            }
            var badge=new Border { CornerRadius=new CornerRadius(5),Background=Ui.Brush(overdue?(IsDark?"#432421":"#FFF0EE"):(IsDark?"#2C2C2E":"#F2F2F7")),Padding=new Thickness(7,3,7,3),VerticalAlignment=VerticalAlignment.Center,Child=Ui.Text(status,10.5,overdue?Red:Muted,FontWeights.Medium) };Grid.SetColumn(badge,2);grid.Children.Add(badge);
            row.ToolTip=task.Title+"\n"+due+" · "+status+(String.IsNullOrEmpty(task.Notes)?"":"\n"+task.Notes);AutomationProperties.SetName(row,"截止待办："+task.Title+"，"+due+"，"+status);
            row.MouseLeftButtonUp+=delegate{controller.Edit(task);};row.MouseEnter+=delegate{row.Background=Ui.Brush(IsDark?"#262628":"#F8F8FA");};row.MouseLeave+=delegate{row.Background=Brushes.Transparent;};
            var menu=new ContextMenu();AddMenu(menu,task.Completed?"恢复为待办":"完成事项",delegate{controller.Toggle(task);},false);AddMenu(menu,"编辑截止待办",delegate{controller.Edit(task);},false);AddMenu(menu,"删除事项",delegate{controller.Delete(task);},false);row.ContextMenu=menu;
            return row;
        }
        UIElement Footer()
        {
            var footer=new DockPanel{Margin=Compact?new Thickness(16,8,16,9):new Thickness(24,11,24,13),LastChildFill=true};
            var right=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(right,Dock.Right);footer.Children.Add(right);
            var undated=Ui.Button("未安排 "+controller.Data.Tasks.Count(t=>!t.DueAt.HasValue),OpenUndated,false,IsDark);undated.FontSize=11;undated.MinHeight=28;undated.Padding=new Thickness(9,4,9,4);right.Children.Add(undated);
            var check=new CheckBox{Content="显示已完成",IsChecked=includeCompleted,Foreground=Muted,FontSize=11,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,0,0)};check.Checked+=delegate{includeCompleted=true;Render();};check.Unchecked+=delegate{includeCompleted=false;Render();};right.Children.Add(check);
            var caption=Ui.Text(expanded?"双击边缘空白处还原 · Esc 退出全屏":Compact?(dayMode?"点按时间添加":"点按日期查看日程"):(dayMode?"点按时间添加 · 点按事项编辑":"朝夕日历 · 点按日期查看日程"),11,Muted,FontWeights.Normal);caption.VerticalAlignment=VerticalAlignment.Center;caption.ToolTip=expanded?"双击卡片边缘、标题旁空白处，恢复原来的大小和位置":"拖动边缘调整大小；双击标题旁的空白处放大";footer.Children.Add(caption);return footer;
        }
        void OpenUndated()
        {
            var dialog=new Window{Title="未安排日期的事项",Width=500,Height=530,Background=Surface,FontFamily=FontFamily,WindowStartupLocation=WindowStartupLocation.CenterScreen};
            var panel=new StackPanel{Margin=new Thickness(22)};
            var tasks=controller.Data.Tasks.Where(t=>!t.DueAt.HasValue).ToList();
            panel.Children.Add(Ui.Text("未安排",25,TextColor,FontWeights.SemiBold));
            var hint=Ui.Text(tasks.Count==0?"没有未安排的事项。":"点击事项，补上日期后就会出现在月历中。",13,Muted,FontWeights.Normal);hint.Margin=new Thickness(0,12,0,20);panel.Children.Add(hint);
            foreach(var task in tasks) { var t=task;var button=Ui.Button((t.Completed?"✓ ":"")+t.Title,delegate{dialog.Close();controller.Edit(t);},false,IsDark);button.HorizontalContentAlignment=HorizontalAlignment.Left;button.Margin=new Thickness(0,0,0,8);var menu=new ContextMenu();var mark=new MenuItem{Header=t.Completed?"恢复为待办":"完成事项"};mark.Click+=delegate{dialog.Close();controller.Toggle(t);};menu.Items.Add(mark);var remove=new MenuItem{Header="删除事项"};remove.Click+=delegate{dialog.Close();controller.Delete(t);};menu.Items.Add(remove);button.ContextMenu=menu;panel.Children.Add(button); }
            dialog.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};dialog.ShowDialog();
        }
        void OpenMenu()
        {
            var menu=new ContextMenu{FontFamily=FontFamily,FontSize=13};
            AddMenu(menu,expanded?"恢复小卡片":"放大到全屏",ToggleExpandedView,false);menu.Items.Add(new Separator());
            AddMenu(menu,"跟随系统主题",delegate{Theme("system");},controller.Data.Settings.ThemeMode=="system");AddMenu(menu,"浅色主题",delegate{Theme("light");},controller.Data.Settings.ThemeMode=="light");AddMenu(menu,"深色主题",delegate{Theme("dark");},controller.Data.Settings.ThemeMode=="dark");
            menu.Items.Add(new Separator());
            AddMenu(menu,"固定在主屏右上角",delegate{if(expanded)ToggleExpandedView();controller.Data.Settings.FollowPrimaryTopRight=!controller.Data.Settings.FollowPrimaryTopRight;ApplyPlacement();controller.Save(true);},controller.Data.Settings.FollowPrimaryTopRight);
            AddMenu(menu,"贴在桌面（其他软件在前）",delegate{if(expanded)ToggleExpandedView();controller.Data.Settings.DesktopMode=!controller.Data.Settings.DesktopMode;if(controller.Data.Settings.DesktopMode)host.Attach();else host.Detach();ApplyPlacement();controller.Save(true);},controller.Data.Settings.DesktopMode);
            AddMenu(menu,"开机启动",delegate{controller.AutoStart=!controller.AutoStart;},controller.AutoStart);
            var size=new MenuItem{Header="日历大小"};menu.Items.Add(size);
            foreach(var choice in new[]{new{Name="迷你",W=560.0,H=500.0},new{Name="紧凑（默认）",W=620.0,H=560.0},new{Name="舒展",W=740.0,H=650.0},new{Name="宽大",W=860.0,H=760.0}}) {
                var c=choice;var item=new MenuItem{Header=c.Name};item.Click+=delegate{if(expanded)ToggleExpandedView();if(host!=null)host.Detach();controller.Data.Settings.Width=c.W;controller.Data.Settings.Height=c.H;Width=c.W;Height=c.H;ClampPlacement();ApplyPlacement();Render();SavePlacement();if(controller.Data.Settings.DesktopMode)host.Attach();ApplyPlacement();};size.Items.Add(item);
            }
            menu.Items.Add(new Separator());AddMenu(menu,"测试提醒",controller.TestReminder,false);AddMenu(menu,"导出备份…",controller.Export,false);AddMenu(menu,"打开数据文件夹",controller.OpenData,false);
            AddMenu(menu,"使用说明",delegate{MessageBox.Show("月历中会列出每天的简要事项，红色圆圈标记今天。\n点击日期进入日视图，查看全天安排和各个时段的日程。\n\n双击标题旁的空白区域或农历文字，可放大到全屏。\n再次双击空白区域或按Esc，可恢复原大小和位置。\n\n默认固定在主屏右上角，切换主屏后会自动跟随。\n在菜单中取消固定后，可拖动年份或日期标题调整位置。\n日历大小可选迷你、紧凑、舒展和宽大。\n\n点击右上角＋或时间轴空白处添加事项。\n点击事项编辑；右键事项可完成、恢复或删除。\n全天事项开启提醒时按当天09:00提醒。\n提醒需要保持程序运行，休眠或退出期间未发出的提醒会在恢复后补发。\n\n日历下方的截止待办每天显示，完成前不会随所选日期隐藏。\n点击下方＋添加，设置标题和截止日期；勾选圆圈完成，右键可编辑或删除。\n\n拖动卡片四边或四角可连续调整长宽。\n上下拖动日程和截止待办之间的分界线，分配两区空间。\n尺寸和分区比例会保存；拖动时按 Esc 取消。\n\n数据保存在本机。版本 2.4.1", "朝夕日历",MessageBoxButton.OK,MessageBoxImage.Information);},false);
            menu.Items.Add(new Separator());AddMenu(menu,"退出",controller.Exit,false);menu.PlacementTarget=this;menu.Placement=PlacementMode.MousePoint;menu.IsOpen=true;
        }
        void Theme(string theme) { controller.Data.Settings.ThemeMode=theme;controller.Save(true);Render(); }
        string PositionTip() { return expanded?"双击还原；Esc 退出全屏":controller.Data.Settings.FollowPrimaryTopRight?"双击放大到全屏；固定在主屏右上角":"双击放大到全屏；按住拖动日历"; }
        static void AddMenu(ContextMenu menu,string title,Action action,bool check) { var item=new MenuItem{Header=title,IsChecked=check};item.Click+=delegate{action();};menu.Items.Add(item); }
        void ScheduleCapture()
        {
            if(String.IsNullOrEmpty(Program.CaptureDirectory))return;
            if(captureTimer!=null)captureTimer.Stop();captureTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(350)};
            captureTimer.Tick+=delegate { captureTimer.Stop();try { Directory.CreateDirectory(Program.CaptureDirectory);FrameworkElement visual=this;if(visual==null||visual.ActualWidth<1)return;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),(int)Math.Ceiling(visual.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(Path.Combine(Program.CaptureDirectory,(dayMode?"day":"month")+(IsDark?"-dark":"-light")+".png")))encoder.Save(output); } catch(Exception ex){ File.WriteAllText(Path.Combine(controller.Store.Root,"capture-error.txt"),ex.ToString()); } };captureTimer.Start();
        }
    }
}
