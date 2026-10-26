using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Daynote
{
    public struct PixelRect
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public override string ToString() { return Left + "," + Top + "," + Right + "," + Bottom; }
    }

    public sealed class DisplayMonitor
    {
        public string Device;
        public bool Primary;
        public PixelRect Bounds;
        public PixelRect Work;
        public uint DpiX = 96, DpiY = 96;
    }

    public sealed class DisplayPlacement
    {
        public DisplayMonitor Monitor;
        public PixelRect Rect;
        public double WidthDip, HeightDip;
    }

    public static class DisplayLayout
    {
        public static List<DisplayMonitor> Monitors()
        {
            var list = new List<DisplayMonitor>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                delegate(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr parameter)
                {
                    var info = new MONITORINFOEX { Size = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                    if (!GetMonitorInfo(monitor, ref info)) return true;
                    uint dx = 96, dy = 96;
                    try { if (GetDpiForMonitor(monitor, 0, out dx, out dy) != 0) dx = dy = 96; }
                    catch (EntryPointNotFoundException) { }
                    catch (DllNotFoundException) { }
                    if (dx == 0) dx = 96; if (dy == 0) dy = 96;
                    list.Add(new DisplayMonitor { Device = info.Device ?? "", Primary = (info.Flags & 1) != 0,
                        Bounds = Convert(info.Monitor), Work = Convert(info.Work), DpiX = dx, DpiY = dy });
                    return true;
                }, IntPtr.Zero);
            return list;
        }

        public static string Signature()
        {
            return Signature(Monitors());
        }

        public static string Signature(IEnumerable<DisplayMonitor> monitors)
        {
            var result = new StringBuilder();
            foreach (var monitor in monitors.OrderBy(m => m.Device, StringComparer.Ordinal))
                result.Append(monitor.Device).Append('|').Append(monitor.Primary ? '1' : '0').Append('|')
                    .Append(monitor.Bounds).Append('|').Append(monitor.Work).Append('|')
                    .Append(monitor.DpiX).Append(',').Append(monitor.DpiY).Append(';');
            return result.ToString();
        }

        public static DisplayPlacement PlanPrimary(double widthDip, double heightDip, double marginDip)
        {
            var monitors = Monitors();
            var primary = monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault();
            if (primary == null) throw new InvalidOperationException("没有可用的显示器工作区域。");
            return Plan(primary, widthDip, heightDip, marginDip);
        }

        // Pure geometry: accepts an explicit monitor, so primary-screen changes,
        // negative origins, taskbars and mixed DPI can be tested without changing
        // any Windows display setting.
        public static DisplayPlacement Plan(DisplayMonitor primary, double widthDip, double heightDip, double marginDip)
        {
            if (primary == null) throw new ArgumentNullException("primary");
            if (primary.Work.Width <= 0 || primary.Work.Height <= 0) throw new ArgumentException("显示器工作区无效。");
            if (Double.IsNaN(widthDip) || Double.IsInfinity(widthDip) || widthDip <= 0) throw new ArgumentOutOfRangeException("widthDip");
            if (Double.IsNaN(heightDip) || Double.IsInfinity(heightDip) || heightDip <= 0) throw new ArgumentOutOfRangeException("heightDip");
            if (Double.IsNaN(marginDip) || Double.IsInfinity(marginDip)) marginDip = 16;
            double sx = Math.Max(1, primary.DpiX) / 96.0, sy = Math.Max(1, primary.DpiY) / 96.0;
            int mx = Math.Max(0, Math.Min(primary.Work.Width / 4, (int)Math.Round(Math.Max(0, marginDip) * sx)));
            int my = Math.Max(0, Math.Min(primary.Work.Height / 4, (int)Math.Round(Math.Max(0, marginDip) * sy)));
            int width = Math.Max(1, Math.Min(primary.Work.Width - 2 * mx, (int)Math.Round(widthDip * sx)));
            int height = Math.Max(1, Math.Min(primary.Work.Height - 2 * my, (int)Math.Round(heightDip * sy)));
            int left = primary.Work.Right - mx - width, top = primary.Work.Top + my;
            return new DisplayPlacement { Monitor = primary, WidthDip = width / sx, HeightDip = height / sy,
                Rect = new PixelRect { Left = left, Top = top, Right = left + width, Bottom = top + height } };
        }

        private static PixelRect Convert(RECT value) { return new PixelRect { Left=value.Left, Top=value.Top, Right=value.Right, Bottom=value.Bottom }; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left,Top,Right,Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
        private struct MONITORINFOEX { public int Size; public RECT Monitor,Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device; }
        private delegate bool MonitorCallback(IntPtr monitor,IntPtr dc,ref RECT rect,IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorCallback callback,IntPtr parameter);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor,ref MONITORINFOEX info);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);
    }
}
