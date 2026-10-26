using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Daynote
{
    // The desktop hierarchy is an Explorer implementation detail, not a public
    // widget API. All attachment failures leave a usable ordinary bottom window.
    // The standard Win32 style/coordinate rules are documented at:
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setparent
    public sealed class DesktopHost : IDisposable
    {
        private readonly Window window;
        private IntPtr handle;
        private IntPtr parent;
        private IntPtr originalOwner;
        private long originalStyle;
        private long originalExStyle;
        private bool captured;
        private bool requested;
        private bool disposed;
        private bool moving;
        private DateTime nextAttempt;
        private HwndSource source;
        private DispatcherTimer surfaceTimer;
        private double surfaceOriginalWidth;
        private double surfaceNudgedWidth;
        private double surfaceOriginalHeight;
        private double surfaceNudgedHeight;
        private bool followsPrimary;
        private bool placingPrimary;
        private bool placementQueued;
        private double requestedWidth = 620, requestedHeight = 560, requestedMargin = 16;
        private double contentScaleX = 1, contentScaleY = 1;
        private double minimumWidth, minimumHeight, maximumWidth, maximumHeight;
        private bool limitsCaptured;
        private FrameworkElement scaledContent;
        private Transform originalContentTransform;
        private readonly DependencyPropertyDescriptor contentDescriptor;
        private bool placingExpanded;
        private string expandedMonitorDevice;
        private ExpandedSnapshot expandedSnapshot;

        private sealed class ExpandedSnapshot
        {
            public RECT Bounds;
            public string MonitorDevice;
            public bool WasAttached, FollowPrimary, Requested, Topmost;
        }

        public bool IsAttached { get; private set; }
        public string LastError { get; private set; }
        public bool NeedsWindowRecreation { get; private set; }
        public bool IsExpanded { get; private set; }
        public double ExpandedWidthDip { get; private set; }
        public double ExpandedHeightDip { get; private set; }

        public DesktopHost(Window window)
        {
            if (window == null) throw new ArgumentNullException("window");
            this.window = window;
            LastError = "";
            contentDescriptor = DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.ContentControl.ContentProperty, typeof(Window));
            if (contentDescriptor != null) contentDescriptor.AddValueChanged(window, ContentChanged);
        }

        public bool Attach()
        {
            if (disposed || IsExpanded) return false;
            requested = true;
            nextAttempt = DateTime.UtcNow.AddSeconds(10);
            handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !IsWindow(handle))
            {
                LastError = "组件窗口尚未就绪。";
                NeedsWindowRecreation = captured;
                return false;
            }
            EnsureVisible();
            if (IsAttached && parent != IntPtr.Zero && IsWindow(parent) && GetParent(handle) == parent)
                return true;

            if (!captured)
            {
                originalStyle = GetLong(handle, GWL_STYLE).ToInt64();
                originalExStyle = GetLong(handle, GWL_EXSTYLE).ToInt64();
                originalOwner = GetLong(handle, GWLP_HWNDPARENT);
                captured = true;
                source = HwndSource.FromHwnd(handle);
                if (source != null) source.AddHook(WindowHook);
            }
            RECT bounds;
            if (!GetWindowRect(handle, out bounds))
                return Fail("无法读取组件位置", Marshal.GetLastWin32Error());
            double logicalWidth = PendingOriginalWidth();
            double logicalHeight = PendingOriginalHeight();
            DisplayMonitor manualMonitor = limitsCaptured && !followsPrimary ? CurrentMonitor() : null;
            double contentWidth = logicalWidth / contentScaleX, contentHeight = logicalHeight / contentScaleY;

            IntPtr target = FindDesktopView();
            if (target == IntPtr.Zero)
                return Fail("未找到 Windows 桌面，暂时使用普通窗口模式", 0);

            // Reparenting across different awareness contexts can force a reset
            // of this process's DPI behavior. Refuse it rather than silently
            // changing the scale of every WPF dialog. The application manifest
            // should request PerMonitorV2, matching modern Explorer.
            try
            {
                IntPtr widgetDpi = GetWindowDpiAwarenessContext(handle);
                IntPtr desktopDpi = GetWindowDpiAwarenessContext(target);
                if (widgetDpi != IntPtr.Zero && desktopDpi != IntPtr.Zero &&
                    !AreDpiAwarenessContextsEqual(widgetDpi, desktopDpi))
                    return Fail("组件与 Windows 桌面的缩放模式不一致，暂时使用普通窗口模式", 0);
            }
            catch (EntryPointNotFoundException) { /* Windows 8 lacks this check. */ }

            try
            {
                // A child of DefView is above the icon list only within this small
                // widget rectangle. It remains below ordinary application windows
                // and belongs to the desktop during Show Desktop / Win+D.
                SetLongChecked(handle, GWL_STYLE,
                    new IntPtr((originalStyle | WS_CHILD | WS_CLIPSIBLINGS) & ~WS_POPUP));
                SetLongChecked(handle, GWL_EXSTYLE,
                    new IntPtr((originalExStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
                SetLastError(0);
                IntPtr previous = SetParent(handle, target);
                int error = Marshal.GetLastWin32Error();
                if (previous == IntPtr.Zero && error != 0)
                    throw new Win32Exception(error);
                if (GetParent(handle) != target)
                    throw new InvalidOperationException("桌面挂接未生效。");
                parent = target;
                POINT point = new POINT(bounds.Left, bounds.Top);
                MapWindowPoints(IntPtr.Zero, target, ref point, 1);
                moving = true;
                try
                {
                    if (!SetWindowPos(handle, HWND_TOP, point.X, point.Y,
                        bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                        SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                finally { moving = false; }
                IsAttached = true;
                NeedsWindowRecreation = false;
                LastError = "";
                if (manualMonitor != null)
                {
                    ConfigurePresentation(contentWidth, contentHeight, manualMonitor.DpiX, manualMonitor.DpiY);
                    RefreshSurface(window.Width, window.Height);
                }
                else RefreshSurface(logicalWidth, logicalHeight);
                return true;
            }
            catch (Exception error)
            {
                Restore(bounds);
                IsAttached = false;
                LastError = "桌面挂接失败，已切换为普通窗口：" + error.Message;
                SendToBack();
                return false;
            }
        }

        public void Detach()
        {
            requested = false;
            if (!captured || handle == IntPtr.Zero || !IsWindow(handle))
            {
                IsAttached = false;
                return;
            }
            RECT bounds;
            if (GetWindowRect(handle, out bounds)) Restore(bounds);
            IsAttached = false;
            parent = IntPtr.Zero;
        }

        public void SendToBack()
        {
            if (disposed || IsExpanded || handle == IntPtr.Zero || !IsWindow(handle)) return;
            // HWND_TOP is local to the desktop child hierarchy, not topmost.
            SetWindowPos(handle, IsAttached ? HWND_TOP : HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public void Maintain()
        {
            if (IsExpanded) { MaintainExpanded(); return; }
            if (disposed || !requested || moving) return;
            if (handle != IntPtr.Zero && !IsWindow(handle))
            {
                IsAttached = false;
                NeedsWindowRecreation = true;
                LastError = "桌面已重建，需要重新创建组件窗口。";
                return;
            }
            if (IsAttached && (parent == IntPtr.Zero || !IsWindow(parent) || GetParent(handle) != parent))
                IsAttached = false;
            if (!IsAttached && DateTime.UtcNow >= nextAttempt)
                Attach();
        }

        // Persist this value instead of Window.Left/Top after native reparenting.
        // WPF Left/Top can then represent the parent client origin.
        public Point GetScreenPosition()
        {
            RECT bounds;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out bounds))
                return new Point(window.Left, window.Top);
            Point point = new Point(bounds.Left, bounds.Top);
            if (source != null && source.CompositionTarget != null)
                point = source.CompositionTarget.TransformFromDevice.Transform(point);
            return point;
        }

        // For custom dragging, these are screen coordinates in WPF DIPs.
        public void MoveTo(double left, double top)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle)) return;
            Point scaled = new Point(left, top);
            if (source != null && source.CompositionTarget != null)
                scaled = source.CompositionTarget.TransformToDevice.Transform(scaled);
            POINT point = new POINT((int)Math.Round(scaled.X), (int)Math.Round(scaled.Y));
            if (IsAttached && parent != IntPtr.Zero)
                MapWindowPoints(IntPtr.Zero, parent, ref point, 1);
            moving = true;
            try
            {
                SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            finally { moving = false; }
        }

        // Clamp this window to the work area of the monitor that already owns
        // most of it (or the nearest monitor when restored into a display gap).
        // All native rectangles and mapping operations use device pixels under
        // the application's PerMonitorV2 manifest; do not clamp to the union of
        // monitor rectangles, which includes non-display gaps on mixed layouts.
        public void EnsureVisible()
        {
            if (disposed) return;
            if (IsExpanded) { ApplyExpandedPlacement(); return; }
            if (handle == IntPtr.Zero) handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !IsWindow(handle)) return;
            if (source == null) source = HwndSource.FromHwnd(handle);
            RECT bounds;
            if (!GetWindowRect(handle, out bounds)) return;
            IntPtr monitor = MonitorFromWindow(handle, 2);
            var info = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
            int width = Math.Min(bounds.Right - bounds.Left, info.Work.Right - info.Work.Left);
            int height = Math.Min(bounds.Bottom - bounds.Top, info.Work.Bottom - info.Work.Top);
            // Resize through WPF first so native WM_SIZE does not become a new
            // unscaled logical Width/Height when a desktop child crosses DPI.
            if (width < bounds.Right - bounds.Left)
                window.Width = window.Width * width / (bounds.Right - bounds.Left);
            if (height < bounds.Bottom - bounds.Top)
                window.Height = window.Height * height / (bounds.Bottom - bounds.Top);
            if (!GetWindowRect(handle, out bounds)) return;
            width = bounds.Right - bounds.Left;
            height = bounds.Bottom - bounds.Top;
            int x = Math.Max(info.Work.Left, Math.Min(bounds.Left, info.Work.Right - width));
            int y = Math.Max(info.Work.Top, Math.Min(bounds.Top, info.Work.Bottom - height));
            if (x == bounds.Left && y == bounds.Top && width == bounds.Right - bounds.Left && height == bounds.Bottom - bounds.Top) return;
            POINT point = new POINT(x, y);
            IntPtr currentParent = GetParent(handle);
            if ((GetLong(handle, GWL_STYLE).ToInt64() & WS_CHILD) != 0 && currentParent != IntPtr.Zero)
                MapWindowPoints(IntPtr.Zero, currentParent, ref point, 1);
            moving = true;
            try
            {
                SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            finally { moving = false; }
        }

        public void PlaceOnPrimary(double widthDip, double heightDip, double marginDip)
        {
            if (disposed || IsExpanded) return;
            if (Double.IsNaN(widthDip) || Double.IsInfinity(widthDip) || widthDip <= 0) throw new ArgumentOutOfRangeException("widthDip");
            if (Double.IsNaN(heightDip) || Double.IsInfinity(heightDip) || heightDip <= 0) throw new ArgumentOutOfRangeException("heightDip");
            requestedWidth = widthDip; requestedHeight = heightDip; requestedMargin = marginDip;
            followsPrimary = true;
            ApplyPrimaryPlacement();
        }

        public void StopFollowingPrimary()
        {
            followsPrimary = false;
            if (IsExpanded && expandedSnapshot != null) expandedSnapshot.FollowPrimary = false;
        }

        public bool EnterExpanded()
        {
            if (disposed) return false;
            if (IsExpanded) return true;
            if (handle == IntPtr.Zero) handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !IsWindow(handle)) { LastError = "日历窗口尚未就绪。"; return false; }
            CompleteSurfaceRefresh();
            RECT compact;
            if (!GetWindowRect(handle, out compact)) { LastError = "无法读取日历当前位置。"; return false; }
            DisplayMonitor monitor = CurrentMonitor();
            if (monitor == null) { LastError = "无法读取当前显示器工作区。"; return false; }
            expandedSnapshot = new ExpandedSnapshot { Bounds = compact, MonitorDevice = monitor.Device,
                WasAttached = IsAttached, FollowPrimary = followsPrimary, Requested = requested, Topmost = window.Topmost };
            expandedMonitorDevice = monitor.Device;
            IsExpanded = true;
            followsPrimary = false;
            try
            {
                // Set IsExpanded before detaching so a queued surface refresh
                // can never put the compact dimensions back over the large view.
                Detach();
                window.WindowState = WindowState.Normal;
                window.Topmost = false;
                ApplyExpandedPlacement();
                if (!window.IsVisible) window.Show();
                if (window.Opacity > 0) window.Activate();
                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                try { ExitExpanded(); } catch { }
                LastError = "无法展开日历：" + ex.Message;
                return false;
            }
        }

        public void ExitExpanded()
        {
            if (!IsExpanded || disposed) return;
            var saved = expandedSnapshot;
            IsExpanded = false;
            if (surfaceTimer != null) { surfaceTimer.Stop(); surfaceTimer = null; }
            followsPrimary = false;
            expandedSnapshot = null;
            ExpandedWidthDip = ExpandedHeightDip = 0;
            if (saved == null || handle == IntPtr.Zero || !IsWindow(handle)) return;
            DisplayMonitor monitor = null;
            foreach (var item in DisplayLayout.Monitors()) if (item.Device == saved.MonitorDevice) { monitor = item; break; }
            if (monitor == null) monitor = CurrentMonitor();
            if (monitor == null) monitor = DisplayLayout.PlanPrimary(requestedWidth, requestedHeight, requestedMargin).Monitor;
            int width = Math.Min(saved.Bounds.Right - saved.Bounds.Left, monitor.Work.Width);
            int height = Math.Min(saved.Bounds.Bottom - saved.Bounds.Top, monitor.Work.Height);
            int left = Math.Max(monitor.Work.Left, Math.Min(saved.Bounds.Left, monitor.Work.Right - width));
            int top = Math.Max(monitor.Work.Top, Math.Min(saved.Bounds.Top, monitor.Work.Bottom - height));
            MovePhysical(left, top);
            ConfigurePresentation(width * 96.0 / monitor.DpiX, height * 96.0 / monitor.DpiY, monitor.DpiX, monitor.DpiY);
            MovePhysical(left, top);
            window.Topmost = saved.Topmost;
            followsPrimary = saved.FollowPrimary;
            requested = saved.Requested;
            if (saved.WasAttached) Attach();
            else RefreshSurface(window.Width, window.Height);
            // In pinned mode the stored rectangle already matches the primary
            // anchor; root can apply its current pinned preference again safely.
            if (followsPrimary) ApplyPrimaryPlacement();
            LastError = "";
        }

        public void MaintainExpanded()
        {
            if (disposed || !IsExpanded) return;
            ApplyExpandedPlacement();
        }

        private void ApplyExpandedPlacement()
        {
            if (disposed || !IsExpanded || placingExpanded || handle == IntPtr.Zero || !IsWindow(handle)) return;
            placingExpanded = true;
            try
            {
                DisplayMonitor monitor = null;
                foreach (var item in DisplayLayout.Monitors()) if (item.Device == expandedMonitorDevice) { monitor = item; break; }
                if (monitor == null) monitor = CurrentMonitor();
                if (monitor == null) monitor = DisplayLayout.PlanPrimary(requestedWidth, requestedHeight, requestedMargin).Monitor;
                expandedMonitorDevice = monitor.Device;
                ExpandedWidthDip = monitor.Work.Width * 96.0 / monitor.DpiX;
                ExpandedHeightDip = monitor.Work.Height * 96.0 / monitor.DpiY;
                MovePhysical(monitor.Work.Left, monitor.Work.Top);
                ConfigurePresentation(ExpandedWidthDip, ExpandedHeightDip, monitor.DpiX, monitor.DpiY);
                MovePhysical(monitor.Work.Left, monitor.Work.Top);
                window.InvalidateVisual();
            }
            finally { placingExpanded = false; }
        }

        private void CompleteSurfaceRefresh()
        {
            if (surfaceTimer == null) return;
            surfaceTimer.Stop();
            if (IsSurfaceNudgeWidth(window.Width, surfaceNudgedWidth)) window.Width = surfaceOriginalWidth;
            if (IsSurfaceNudgeWidth(window.Height, surfaceNudgedHeight)) window.Height = surfaceOriginalHeight;
            surfaceTimer = null;
        }

        public string PlacementDiagnostics()
        {
            try
            {
                if (handle == IntPtr.Zero) handle = new WindowInteropHelper(window).Handle;
                RECT current; GetWindowRect(handle, out current);
                var plan = DisplayLayout.PlanPrimary(requestedWidth, requestedHeight, requestedMargin);
                return "followPrimary=" + followsPrimary + "\nrequestedDip=" + requestedWidth + "," + requestedHeight +
                    "\nexpanded=" + IsExpanded + "\nexpandedDip=" + ExpandedWidthDip + "," + ExpandedHeightDip +
                    "\nprimary=" + plan.Monitor.Device + "\nprimaryDpi=" + plan.Monitor.DpiX + "," + plan.Monitor.DpiY +
                    "\nprimaryWorkPx=" + plan.Monitor.Work + "\ntargetRectPx=" + plan.Rect +
                    "\nactualRectPx=" + current.Left + "," + current.Top + "," + current.Right + "," + current.Bottom +
                    "\ncontentScale=" + contentScaleX + "," + contentScaleY;
            }
            catch (Exception ex) { return "placementError=" + ex.Message; }
        }

        private void ApplyPrimaryPlacement(DisplayPlacement placement = null)
        {
            if (disposed || IsExpanded || !followsPrimary || placingPrimary) return;
            if (handle == IntPtr.Zero) handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !IsWindow(handle)) return;
            placingPrimary = true;
            try
            {
                var plan = placement ?? DisplayLayout.PlanPrimary(requestedWidth, requestedHeight, requestedMargin);
                // Move first: a top-level WPF window may receive WM_DPICHANGED
                // synchronously on entering the primary monitor.
                MovePhysical(plan.Rect.Left, plan.Rect.Top);
                ConfigurePresentation(plan.WidthDip, plan.HeightDip, plan.Monitor.DpiX, plan.Monitor.DpiY);
                MovePhysical(plan.Rect.Left, plan.Rect.Top);
                window.InvalidateVisual();
            }
            catch (Exception ex) { LastError = "无法定位到主显示器：" + ex.Message; }
            finally { placingPrimary = false; }
        }

        private void ConfigurePresentation(double widthDip, double heightDip, uint dpiX, uint dpiY)
        {
            source = HwndSource.FromHwnd(handle);
            double sx = 1, sy = 1;
            if (source != null && source.CompositionTarget != null)
            {
                sx = source.CompositionTarget.TransformToDevice.M11;
                sy = source.CompositionTarget.TransformToDevice.M22;
            }
            if (sx <= 0) sx = 1; if (sy <= 0) sy = 1;
            contentScaleX = dpiX / 96.0 / sx; contentScaleY = dpiY / 96.0 / sy;
            if (!limitsCaptured)
            {
                minimumWidth=window.MinWidth; minimumHeight=window.MinHeight;
                maximumWidth=window.MaxWidth; maximumHeight=window.MaxHeight; limitsCaptured=true;
            }
            double width = widthDip * contentScaleX, height = heightDip * contentScaleY;
            window.MinWidth = Math.Min(minimumWidth * contentScaleX, width);
            window.MinHeight = Math.Min(minimumHeight * contentScaleY, height);
            window.MaxWidth = Math.Max(width, maximumWidth * contentScaleX);
            window.MaxHeight = Math.Max(height, maximumHeight * contentScaleY);
            window.Width = width; window.Height = height;
            ApplyContentScale();
        }

        private DisplayMonitor CurrentMonitor()
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
            IntPtr monitor = MonitorFromWindow(handle, 2);
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
            foreach (var item in DisplayLayout.Monitors())
                if (item.Bounds.Left == info.Monitor.Left && item.Bounds.Top == info.Monitor.Top &&
                    item.Bounds.Right == info.Monitor.Right && item.Bounds.Bottom == info.Monitor.Bottom) return item;
            return null;
        }

        private void MovePhysical(int x, int y)
        {
            POINT point = new POINT(x, y);
            IntPtr currentParent = GetParent(handle);
            if ((GetLong(handle, GWL_STYLE).ToInt64() & WS_CHILD) != 0 && currentParent != IntPtr.Zero)
                MapWindowPoints(IntPtr.Zero, currentParent, ref point, 1);
            SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private void ContentChanged(object sender, EventArgs e)
        {
            ApplyContentScale();
            if (IsExpanded) QueueExpandedPlacement();
            else if (followsPrimary) QueuePrimaryPlacement();
        }

        private void ApplyContentScale()
        {
            var content = window.Content as FrameworkElement;
            if (content == null) return;
            if (content != scaledContent)
            {
                scaledContent = content;
                originalContentTransform = content.LayoutTransform;
            }
            if (Math.Abs(contentScaleX - 1) < .0001 && Math.Abs(contentScaleY - 1) < .0001)
                content.LayoutTransform = originalContentTransform ?? Transform.Identity;
            else
            {
                var transform = new TransformGroup();
                if (originalContentTransform != null && !originalContentTransform.Value.IsIdentity)
                    transform.Children.Add(originalContentTransform);
                transform.Children.Add(new ScaleTransform(contentScaleX, contentScaleY));
                content.LayoutTransform = transform;
            }
        }

        private void QueuePrimaryPlacement()
        {
            if (placementQueued || disposed || IsExpanded || !followsPrimary) return;
            placementQueued = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                placementQueued = false;
                if (!disposed && !IsExpanded && followsPrimary) ApplyPrimaryPlacement();
            }));
        }

        private void QueueExpandedPlacement()
        {
            if (placementQueued || disposed || !IsExpanded) return;
            placementQueued = true;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                placementQueued = false;
                if (!disposed && IsExpanded) ApplyExpandedPlacement();
            }));
        }

        public void Dispose()
        {
            if (disposed) return;
            Detach();
            if (surfaceTimer != null)
            {
                surfaceTimer.Stop();
                if (IsSurfaceNudgeWidth(window.Width, surfaceNudgedWidth)) window.Width = surfaceOriginalWidth;
                if (IsSurfaceNudgeWidth(window.Height, surfaceNudgedHeight)) window.Height = surfaceOriginalHeight;
                surfaceTimer = null;
            }
            if (source != null && !source.IsDisposed) source.RemoveHook(WindowHook);
            if (contentDescriptor != null) contentDescriptor.RemoveValueChanged(window, ContentChanged);
            disposed = true;
        }

        private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WM_NCDESTROY)
            {
                IsAttached = false;
                NeedsWindowRecreation = requested && !disposed;
            }
            if (message == 0x02E0 && followsPrimary && !placingPrimary) QueuePrimaryPlacement();
            if ((message == 0x007E || message == 0x001A) && followsPrimary && !placingPrimary) QueuePrimaryPlacement();
            if ((message == 0x02E0 || message == 0x007E || message == 0x001A) && IsExpanded && !placingExpanded) QueueExpandedPlacement();
            return IntPtr.Zero;
        }

        private bool Fail(string message, int error)
        {
            IsAttached = false;
            LastError = error == 0 ? message : message + "：" + new Win32Exception(error).Message;
            SendToBack();
            return false;
        }

        private void Restore(RECT bounds)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle)) return;
            double logicalWidth = PendingOriginalWidth();
            double logicalHeight = PendingOriginalHeight();
            DisplayMonitor manualMonitor = limitsCaptured && !followsPrimary ? CurrentMonitor() : null;
            double contentWidth = logicalWidth / contentScaleX, contentHeight = logicalHeight / contentScaleY;
            moving = true;
            try
            {
                SetParent(handle, IntPtr.Zero);
                SetLong(handle, GWL_STYLE, new IntPtr(originalStyle));
                SetLong(handle, GWL_EXSTYLE, new IntPtr(originalExStyle));
                if (originalOwner == IntPtr.Zero || IsWindow(originalOwner))
                    SetLong(handle, GWLP_HWNDPARENT, originalOwner);
                SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top,
                    bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                if (manualMonitor != null)
                {
                    ConfigurePresentation(contentWidth, contentHeight, manualMonitor.DpiX, manualMonitor.DpiY);
                    RefreshSurface(window.Width, window.Height);
                }
                else RefreshSurface(logicalWidth, logicalHeight);
            }
            finally { moving = false; }
        }

        private double PendingOriginalWidth()
        {
            return surfaceTimer != null && IsSurfaceNudgeWidth(window.Width, surfaceNudgedWidth) ? surfaceOriginalWidth : window.Width;
        }

        private double PendingOriginalHeight()
        {
            return surfaceTimer != null && IsSurfaceNudgeWidth(window.Height, surfaceNudgedHeight) ? surfaceOriginalHeight : window.Height;
        }

        private void RefreshSurface(double logicalWidth, double logicalHeight)
        {
            // On .NET Framework's layered WPF windows, changing the native
            // parent discards the backing surface while WPF retains its size.
            // InvalidateVisual alone does not recreate it. One real size change
            // forces a new surface; restore the exact size after a render turn.
            // Verified by own-HWND PrintWindow pixel checks on Windows 11 26200.
            if (disposed || !window.IsLoaded || handle == IntPtr.Zero || !IsWindow(handle)) return;
            if (surfaceTimer != null)
            {
                surfaceTimer.Stop();
                if (IsSurfaceNudgeWidth(window.Width, surfaceNudgedWidth))
                    window.Width = surfaceOriginalWidth;
                if (IsSurfaceNudgeWidth(window.Height, surfaceNudgedHeight))
                    window.Height = surfaceOriginalHeight;
            }
            double previousWidth = logicalWidth;
            double previousHeight = logicalHeight;
            double measuredWidth = Double.IsNaN(previousWidth) ? window.ActualWidth : previousWidth;
            double measuredHeight = Double.IsNaN(previousHeight) ? window.ActualHeight : previousHeight;
            double nudgedWidth = measuredWidth >= window.MaxWidth ? measuredWidth - 1 : measuredWidth + 1;
            double nudgedHeight = measuredHeight >= window.MaxHeight ? measuredHeight - 1 : measuredHeight + 1;
            surfaceOriginalWidth = previousWidth;
            surfaceNudgedWidth = nudgedWidth;
            surfaceOriginalHeight = previousHeight;
            surfaceNudgedHeight = nudgedHeight;
            window.Width = nudgedWidth;
            window.Height = nudgedHeight;
            window.InvalidateVisual();
            surfaceTimer = new DispatcherTimer(DispatcherPriority.Render, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(60) };
            var timer = surfaceTimer;
            timer.Tick += delegate {
                timer.Stop();
                if (surfaceTimer == timer) surfaceTimer = null;
                if (disposed || handle == IntPtr.Zero || !IsWindow(handle)) return;
                if (IsExpanded) { ApplyExpandedPlacement(); return; }
                // Do not overwrite a new width chosen by the user meanwhile.
                if (IsSurfaceNudgeWidth(window.Width, nudgedWidth)) window.Width = previousWidth;
                if (IsSurfaceNudgeWidth(window.Height, nudgedHeight)) window.Height = previousHeight;
                if (followsPrimary) ApplyPrimaryPlacement(); else EnsureVisible();
            };
            timer.Start();
        }

        private static bool IsSurfaceNudgeWidth(double actual, double expected)
        {
            // At 150% DPI WPF can report 861.333 DIPs for a requested861-DIP
            // nudge. Accept the sub-pixel conversion error while preserving a
            // new size selected by the user (the app's size presets differ by
            // dozens of DIPs). The same rule is used for rapid reparenting.
            return !Double.IsNaN(actual) && !Double.IsNaN(expected) && Math.Abs(actual - expected) <= .75;
        }

        private static IntPtr FindDesktopView()
        {
            IntPtr progman = FindWindow("Progman", null);
            IntPtr found = progman == IntPtr.Zero ? IntPtr.Zero :
                FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (found != IntPtr.Zero) return found;
            EnumWindows(delegate(IntPtr candidate, IntPtr unused)
            {
                StringBuilder name = new StringBuilder(256);
                GetClassName(candidate, name, name.Capacity);
                if (name.ToString() == "WorkerW" || name.ToString() == "Progman")
                {
                    found = FindWindowEx(candidate, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (found != IntPtr.Zero) return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static void SetLongChecked(IntPtr hwnd, int index, IntPtr value)
        {
            SetLastError(0);
            IntPtr result = SetLong(hwnd, index, value);
            int error = Marshal.GetLastWin32Error();
            if (result == IntPtr.Zero && error != 0) throw new Win32Exception(error);
        }
        private static IntPtr GetLong(IntPtr hwnd, int index)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, index) : new IntPtr(GetWindowLong(hwnd, index));
        }
        private static IntPtr SetLong(IntPtr hwnd, int index, IntPtr value)
        {
            return IntPtr.Size == 8 ? SetWindowLongPtr(hwnd, index, value) : new IntPtr(SetWindowLong(hwnd, index, value.ToInt32()));
        }

        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8, WM_NCDESTROY = 0x82;
        private const long WS_CHILD = 0x40000000L, WS_POPUP = 0x80000000L, WS_CLIPSIBLINGS = 0x04000000L;
        private const long WS_EX_TOOLWINDOW = 0x80L, WS_EX_APPWINDOW = 0x40000L;
        private const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;
        private static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_BOTTOM = new IntPtr(1);
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public uint Flags; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out RECT bounds);
        [DllImport("user32.dll")] private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT point, uint count);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    }
}
