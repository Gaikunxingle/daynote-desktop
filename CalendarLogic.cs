using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Daynote
{
    public sealed class CalendarSlot
    {
        public TaskItem Task;
        public double StartMinute;
        public double EndMinute;
        // Only for reserving enough visual room for the smallest readable card.
        public double CollisionEndMinute;
        public int Column;
        public int ColumnCount;
        public bool ContinuationBefore;
        public bool ContinuationAfter;
    }

    public static class CalendarLogic
    {
        private static readonly ChineseLunisolarCalendar Lunar = new ChineseLunisolarCalendar();
        private static readonly string[] LunarMonths = { "正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月" };
        private static readonly string[] LunarDays = { "", "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十", "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十", "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十" };

        public static string LunarShort(DateTime date)
        {
            if (!IsLunarSupported(date)) return "";
            int day = Lunar.GetDayOfMonth(date);
            return day == 1 ? LunarMonth(date) : LunarDays[day];
        }

        public static string LunarLong(DateTime date)
        {
            if (!IsLunarSupported(date)) return "";
            return "农历" + LunarMonth(date) + LunarDays[Lunar.GetDayOfMonth(date)];
        }

        private static bool IsLunarSupported(DateTime date)
        {
            return date >= Lunar.MinSupportedDateTime && date <= Lunar.MaxSupportedDateTime;
        }

        private static string LunarMonth(DateTime date)
        {
            int year = Lunar.GetYear(date), month = Lunar.GetMonth(date), leap = Lunar.GetLeapMonth(year);
            bool isLeap = leap > 0 && month == leap;
            int ordinaryMonth = leap > 0 && month >= leap ? month - 1 : month;
            return (isLeap ? "闰" : "") + LunarMonths[ordinaryMonth - 1];
        }

        public static DateTime MonthGridStart(DateTime month)
        {
            DateTime first = new DateTime(month.Year, month.Month, 1);
            int offset = (int)first.DayOfWeek;
            return first.Ticks >= TimeSpan.FromDays(offset).Ticks ? first.AddDays(-offset) : DateTime.MinValue;
        }

        public static int MonthRowCount(DateTime month)
        {
            DateTime first = new DateTime(month.Year, month.Month, 1);
            return ((int)first.DayOfWeek + DateTime.DaysInMonth(month.Year, month.Month) + 6) / 7;
        }

        public static DateTime? End(TaskItem task)
        {
            if (task == null || !task.DueAt.HasValue || !task.EndAt.HasValue || task.EndAt.Value <= task.DueAt.Value) return null;
            return task.EndAt;
        }

        public static List<TaskItem> TasksForDay(IEnumerable<TaskItem> tasks, DateTime day, bool includeCompleted)
        {
            DateTime start = day.Date;
            DateTime finish = NextDay(start);
            if (tasks == null) return new List<TaskItem>();
            return tasks.Where(t => t != null && t.DueAt.HasValue && (includeCompleted || !t.Completed))
                .Where(t => !t.HasTime || !End(t).HasValue
                    ? t.DueAt.Value.Date == start
                    : t.DueAt.Value < finish && End(t).Value > start)
                .OrderBy(t => t.HasTime ? 1 : 0)
                .ThenBy(t => t.DueAt.Value)
                .ThenBy(t => t.Id, StringComparer.Ordinal)
                .ToList();
        }

        public static List<CalendarSlot> LayoutDay(IEnumerable<TaskItem> tasks, DateTime day, bool includeCompleted)
        {
            DateTime start = day.Date;
            DateTime finish = NextDay(start);
            var slots = new List<CalendarSlot>();
            foreach (var task in TasksForDay(tasks, start, includeCompleted).Where(t => t.HasTime))
            {
                DateTime actualStart = task.DueAt.Value;
                DateTime? actualEnd = End(task);
                double from = Math.Max(0, (actualStart - start).TotalMinutes);
                // A missing end gets a visual footprint, never a fabricated stored end.
                double until = actualEnd.HasValue ? Math.Min(1440, (actualEnd.Value - start).TotalMinutes) : Math.Min(1440, from + 30);
                slots.Add(new CalendarSlot
                {
                    Task = task,
                    StartMinute = from,
                    EndMinute = until,
                    CollisionEndMinute = Math.Min(1440, Math.Max(until, from + 30)),
                    ContinuationBefore = actualStart < start,
                    ContinuationAfter = actualEnd.HasValue && actualEnd.Value > finish
                });
            }
            slots = slots.OrderBy(s => s.StartMinute).ThenByDescending(s => s.CollisionEndMinute).ThenBy(s => s.Task.Id, StringComparer.Ordinal).ToList();
            var group = new List<CalendarSlot>();
            var columnEnds = new List<double>();
            double groupEnd = -1;
            foreach (var slot in slots)
            {
                // Equal endpoints do not overlap: a 10:00 ending frees that column at 10:00.
                if (group.Count > 0 && slot.StartMinute >= groupEnd)
                {
                    SetColumnCount(group, columnEnds.Count);
                    group.Clear();
                    columnEnds.Clear();
                    groupEnd = -1;
                }
                int column = columnEnds.FindIndex(end => end <= slot.StartMinute);
                if (column < 0) { column = columnEnds.Count; columnEnds.Add(slot.CollisionEndMinute); }
                else columnEnds[column] = slot.CollisionEndMinute;
                slot.Column = column;
                group.Add(slot);
                groupEnd = Math.Max(groupEnd, slot.CollisionEndMinute);
            }
            SetColumnCount(group, columnEnds.Count);
            return slots;
        }

        private static void SetColumnCount(IEnumerable<CalendarSlot> group, int count)
        {
            foreach (var slot in group) slot.ColumnCount = Math.Max(1, count);
        }

        private static DateTime NextDay(DateTime day)
        {
            return day == DateTime.MaxValue.Date ? DateTime.MaxValue : day.AddDays(1);
        }
    }
}
