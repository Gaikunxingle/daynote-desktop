using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("朝夕日历")]
[assembly: AssemblyProduct("朝夕提醒")]
[assembly: AssemblyVersion("2.2.0.0")]
[assembly: AssemblyFileVersion("2.2.0.0")]

namespace Daynote
{
    public static class Program
    {
        internal static string Executable { get { return Assembly.GetExecutingAssembly().Location; } }
        internal static string DataRoot;
        internal static bool Preview, Inspect;
        internal static string CaptureDirectory;
        internal static DateTime? StartDay;
        [STAThread]
        public static void Main(string[] args)
        {
            DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Daynote", "Data");
            for (int i = 0; i < args.Length; i++) {
                if (args[i] == "--data-dir" && i + 1 < args.Length) DataRoot = Path.GetFullPath(args[++i]);
                else if (args[i] == "--capture-dir" && i + 1 < args.Length) CaptureDirectory = Path.GetFullPath(args[++i]);
                else if (args[i] == "--day" && i + 1 < args.Length) { DateTime value; if(DateTime.TryParseExact(args[++i],"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out value)) StartDay=value; }
            }
            Preview = args.Contains("--preview");
            Inspect = args.Contains("--inspect");
            string key;
            using (var sha = System.Security.Cryptography.SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(DataRoot.ToLowerInvariant()))).Replace("-", "").Substring(0, 20);
            string name = "Local\\Daynote." + key;
            bool created;
            using (var mutex = new Mutex(true, name, out created))
            {
                if (args.Contains("--quit")) { Signal(name + ".Exit"); return; }
                if (!created) { Signal(name + ".Open"); return; }
                using (var open = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Open"))
                using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Exit"))
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    AppController controller = null;
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
                        try { Directory.CreateDirectory(DataRoot); File.AppendAllText(Path.Combine(DataRoot, "error.log"), DateTime.Now.ToString("s") + " " + e.Exception + Environment.NewLine); } catch { }
                        MessageBox.Show("操作未能完成，请重试。事项数据保存在本机。\n" + e.Exception.Message, "提醒事项", MessageBoxButton.OK, MessageBoxImage.Warning);
                        e.Handled = true;
                    };
                    app.Startup += delegate {
                        controller = new AppController(app);
                        ThreadPool.RegisterWaitForSingleObject(open, delegate { app.Dispatcher.BeginInvoke(new Action(controller.ShowPanel)); }, null, -1, false);
                        ThreadPool.RegisterWaitForSingleObject(quit, delegate { app.Dispatcher.BeginInvoke(new Action(controller.Exit)); }, null, -1, false);
                        controller.Start();
                    };
                    app.Run();
                }
            }
        }
        static void Signal(string name) { try { using (var signal = EventWaitHandle.OpenExisting(name)) signal.Set(); } catch (WaitHandleCannotBeOpenedException) { } }
    }

    public sealed class AppController
    {
        readonly Application app;
        public readonly DataStore Store;
        public AppData Data;
        MainWindow window;
        Forms.NotifyIcon tray;
        DispatcherTimer timer;
        DispatcherTimer displayRefreshTimer;
        string displaySignature;
        bool rebuilding;
        CalendarViewState lastViewState;
        bool exiting, editing, pendingSave, saveWarningShown;
        DateTime day;
        int ticks;
        readonly List<Window> notices = new List<Window>();
        readonly HashSet<string> notifiedThisRun = new HashSet<string>();
        public bool Dark {
            get {
                if (Data.Settings.ThemeMode == "dark") return true;
                if (Data.Settings.ThemeMode == "light") return false;
                try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) return k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 1)) == 0; } catch { return false; }
            }
        }
        public AppController(Application app) { this.app = app; Store = new DataStore(Program.DataRoot); Data = Store.Load(); }
        public void Start()
        {
            if (Program.Preview && Data.Tasks.Count == 0) {
                Data.Settings.DesktopMode = false;
                DateTime today=DateTime.Today;
                Data.Tasks.Add(new TaskItem { Title="门诊", Notes="演示事项：全天安排。", DueAt=today, HasTime=false,ColorId="teal" });
                Data.Tasks.Add(new TaskItem { Title="课题讨论", Notes="准备研究进展与待讨论的问题。",DueAt=today.AddHours(9),EndAt=today.AddHours(10.5),ColorId="blue" });
                Data.Tasks.Add(new TaskItem { Title="阅读文献", Notes="整理核心结论和参考资料。",DueAt=today.AddHours(11),EndAt=today.AddHours(12),ColorId="purple" });
                Data.Tasks.Add(new TaskItem { Title="整理实验记录", Notes="核对结果并补充备注。",DueAt=today.AddHours(14),EndAt=today.AddHours(15.5),ColorId="orange" });
                Data.Tasks.Add(new TaskItem { Title="线上沟通", Notes="演示重叠日程会并排显示。",DueAt=today.AddHours(14.5),EndAt=today.AddHours(15.25),ColorId="green" });
                Data.Tasks.Add(new TaskItem { Title="散步",DueAt=today.AddHours(18),EndAt=today.AddHours(18.5),ColorId="teal" });
                Data.Tasks.Add(new TaskItem { Title="提交项目报告",DueAt=today.AddDays(1).AddHours(18),ColorId="orange" });
                Data.Tasks.Add(new TaskItem { Title="课程准备",DueAt=today.AddDays(2).AddHours(9),EndAt=today.AddDays(2).AddHours(11),ColorId="blue" });
                Data.Tasks.Add(new TaskItem { Title="整理本周资料",DueAt=today.AddDays(-3),HasTime=false,Completed=true,ColorId="purple" });
                Data.Tasks.Add(new TaskItem { Title="待安排的阅读计划",Notes="未设日期的事项会保留在“未安排”中。",ColorId="green" });
                Store.Save(Data);
            }
            tray = new Forms.NotifyIcon();
            tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Program.Executable);
            tray.Text = "朝夕 · 日历";
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示日历", null, delegate { app.Dispatcher.Invoke(new Action(ShowPanel)); });
            menu.Items.Add("添加提醒", null, delegate { app.Dispatcher.Invoke(new Action(delegate { Edit(null); })); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { app.Dispatcher.Invoke(new Action(Exit)); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowPanel(); };
            tray.Visible = true;
            CreateWindow();
            if (!Save(true)) pendingSave = true;
            displaySignature = DisplayLayout.Signature();
            displayRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            displayRefreshTimer.Tick += delegate { displayRefreshTimer.Stop(); if(!exiting) RebuildForDisplayChange(); };
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            day = DateTime.Today;
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += Tick;
            timer.Start();
            if (!String.IsNullOrEmpty(Store.LoadWarning)) MessageBox.Show(Store.LoadWarning, "数据恢复提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        void CreateWindow(CalendarViewState state = null)
        {
            if (exiting || window != null) return;
            MainWindow created = new MainWindow(this);
            window = created;
            if(state != null) created.RestoreViewState(state);
            created.Closed += delegate {
                if(!Object.ReferenceEquals(window,created)) return;
                lastViewState=created.CaptureViewState();window=null;
                if(!exiting&&!rebuilding) app.Dispatcher.BeginInvoke(new Action(delegate { if(!exiting&&window==null)CreateWindow(lastViewState); }),DispatcherPriority.Background);
            };
            created.Show();
        }
        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if(exiting || app.Dispatcher.HasShutdownStarted)return;
            app.Dispatcher.BeginInvoke(new Action(delegate { if(!exiting)ScheduleDisplayRefresh(); }));
        }
        void ScheduleDisplayRefresh()
        {
            if(exiting||displayRefreshTimer==null)return;
            displaySignature=DisplayLayout.Signature();
            displayRefreshTimer.Stop();displayRefreshTimer.Start();
        }
        void RebuildForDisplayChange()
        {
            if(exiting||rebuilding)return;
            rebuilding=true;
            try {
                CalendarViewState state=window==null?lastViewState:window.CaptureViewState();
                MainWindow previous=window;
                if(previous!=null) previous.Close();
                window=null;
                displaySignature=DisplayLayout.Signature();
                CreateWindow(state);
            } finally { rebuilding=false; }
        }
        void Tick(object sender, EventArgs e)
        {
            if (exiting) return;
            string currentSignature=DisplayLayout.Signature();
            if(currentSignature!=displaySignature)ScheduleDisplayRefresh();
            if(window==null)return;
            if (++ticks % 3 == 0) window.MaintainDesktop();
            if(window==null)return;
            if (day != DateTime.Today || window.IsDark != Dark) { day = DateTime.Today; window.Render(); }
            if (pendingSave && Save(false)) pendingSave = false;
            bool notified = false;
            foreach (var task in Data.Tasks.Where(t => ReminderEngine.ShouldNotify(t, DateTime.Now)).ToList()) {
                if (notifiedThisRun.Contains(task.Id)) continue;
                Notify(task);
                task.NotifiedAt = DateTime.Now;
                notifiedThisRun.Add(task.Id);
                if (!Save(false)) { pendingSave=true; if(!saveWarningShown) { saveWarningShown=true; tray.ShowBalloonTip(8000,"提醒事项保存失败","提醒仍会显示，程序将自动重试保存。请检查磁盘空间。",Forms.ToolTipIcon.Warning); } }
                notified = true;
            }
            if(notified) window.Render();
        }
        public bool Save(bool report)
        {
            try { Store.Save(Data); return true; }
            catch (Exception ex) {
                if (report) MessageBox.Show("保存失败，本次更改尚未写入磁盘。\n" + ex.Message, "提醒事项", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        public void Edit(TaskItem task, DateTime? initialDate = null)
        {
            if (editing) return;
            editing = true;
            try {
                var editor = new TaskEditor(task, Dark, initialDate ?? (window == null ? DateTime.Today : window.SelectedDate));
                editor.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                if (editor.ShowDialog() == true) {
                    var old = Data.Tasks.ToList();
                    if (task == null) Data.Tasks.Add(editor.Result);
                    else { int at = Data.Tasks.IndexOf(task); Data.Tasks[at] = editor.Result; }
                    if (!Save(true)) Data.Tasks = old;
                    else notifiedThisRun.Remove(editor.Result.Id);
                    window.Render();
                }
            } finally { editing = false; }
        }
        public void Toggle(TaskItem task)
        {
            task.Completed = !task.Completed;
            if (!Save(true)) task.Completed = !task.Completed;
            window.Render();
        }
        public void Delete(TaskItem task)
        {
            if (MessageBox.Show("删除“" + task.Title + "”？", "删除提醒", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            int index = Data.Tasks.IndexOf(task); Data.Tasks.Remove(task);
            if (!Save(true)) Data.Tasks.Insert(index, task);
            window.Render();
        }
        public void ShowPanel() { if(window==null)CreateWindow(lastViewState);if(window!=null)window.Reveal(); }
        public void Export()
        {
            var dialog = new SaveFileDialog { Title = "导出提醒事项备份", Filter = "提醒事项备份 (*.json)|*.json", FileName = "朝夕备份_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".json" };
            if (dialog.ShowDialog() == true) { if (Save(true)) File.Copy(Store.FilePath, dialog.FileName, true); }
        }
        public void OpenData() { Process.Start("explorer.exe", Store.Root); }
        public bool AutoStart {
            get { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return k != null && k.GetValue("Daynote") != null; }
            set { using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) { if (value) k.SetValue("Daynote", "\"" + Program.Executable + "\""); else k.DeleteValue("Daynote", false); } }
        }
        public void TestReminder() { Notify(new TaskItem { Title = "提醒已准备好", Notes = "这是测试提醒。真实事项会按你设置的时间弹出。", DueAt = DateTime.Now, HasTime = true }); }
        void Notify(TaskItem task)
        {
            var notice = new Window { Title = "朝夕提醒", Width = 370, SizeToContent = SizeToContent.Height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = Program.Inspect, ShowActivated = false, Topmost = true, FontFamily = new FontFamily("Microsoft YaHei UI") };
            var body = new StackPanel();
            body.Children.Add(Ui.Text("提醒时间到了", 12, Ui.Brush("#FF3B30"), FontWeights.SemiBold));
            var title = Ui.Text(task.Title, 18, Dark ? Brushes.White : Ui.Brush("#20293A"), FontWeights.SemiBold); title.Margin = new Thickness(0, 12, 0, 8); body.Children.Add(title);
            body.Children.Add(Ui.Text(ReminderEngine.FormatDue(task), 12, Ui.Brush(Dark ? "#ADB6C8" : "#667085"), FontWeights.Normal));
            if (!String.IsNullOrEmpty(task.Notes)) { var notes = Ui.Text(task.Notes.Length > 160 ? task.Notes.Substring(0,160) + "…" : task.Notes, 13, Ui.Brush(Dark ? "#C9D0DC" : "#566074"), FontWeights.Normal); notes.Margin = new Thickness(0,10,0,0); body.Children.Add(notes); }
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,18,0,0) };
            var close = Ui.Button("知道了", delegate { notice.Close(); }, false, Dark); actions.Children.Add(close);
            if (Data.Tasks.Contains(task)) actions.Children.Add(Ui.Button("完成事项", delegate { var current=Data.Tasks.FirstOrDefault(t=>t.Id==task.Id); if (current!=null && !current.Completed) Toggle(current); notice.Close(); }, true, Dark));
            body.Children.Add(actions);
            notice.Content = new Border { Background = Ui.Brush(Dark ? "#222A38" : "#FFFFFF"), BorderBrush = Ui.Brush(Dark ? "#3A465A" : "#D8E2F7"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(22), Margin = new Thickness(6), Child = body };
            notices.Add(notice);
            notice.Loaded += delegate { PositionNotices(); };
            notice.Closed += delegate { notices.Remove(notice); PositionNotices(); };
            notice.Show();
            System.Media.SystemSounds.Asterisk.Play();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            timeout.Tick += delegate { timeout.Stop(); if (notices.Contains(notice)) notice.Close(); };
            notice.Closed += delegate { timeout.Stop(); };
            timeout.Start();
        }
        void PositionNotices() { double bottom = SystemParameters.WorkArea.Bottom - 16; foreach (var n in notices.AsEnumerable().Reverse()) { n.Left = SystemParameters.WorkArea.Right - n.Width - 16; n.Top = Math.Max(SystemParameters.WorkArea.Top, bottom - n.ActualHeight); bottom = n.Top - 8; } }
        public void Exit()
        {
            if (exiting) return;
            if (window != null) window.SavePlacement();
            exiting = true;
            if (timer != null) timer.Stop();
            if (displayRefreshTimer != null) displayRefreshTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            foreach (var n in notices.ToArray()) n.Close();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (window != null) window.Close();
            app.Shutdown();
        }
    }

    static class Ui
    {
        public static SolidColorBrush Brush(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }
        public static TextBlock Text(string text, double size, Brush color, FontWeight weight) { return new TextBlock { Text = text, FontSize = size, Foreground = color, FontWeight = weight, TextWrapping = TextWrapping.Wrap }; }
        public static Button Button(string label, Action action, bool accent, bool dark)
        {
            var button = new Button { Content = label, Padding = new Thickness(13,8,13,8), Margin = new Thickness(0,0,6,0), FontSize = 13, Cursor = Cursors.Hand, Foreground = accent ? Brushes.White : Brush(dark ? "#F2F2F7" : "#3A3A3C"), Background = Brush(accent ? "#FF3B30" : dark ? "#2C2C2E" : "#F2F2F7"), BorderThickness = new Thickness(0), MinHeight = 34 };
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9)); border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content); template.VisualTree = border; button.Template = template;
            button.MouseEnter += delegate { button.Opacity = .78; }; button.MouseLeave += delegate { button.Opacity = 1; };
            button.Click += delegate { action(); };
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            return button;
        }
    }

}
