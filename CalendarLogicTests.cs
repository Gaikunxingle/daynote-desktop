using System;
using System.Collections.Generic;
using System.Linq;
using Daynote;

public static class CalendarLogicTests
{
    private static int count;
    private static void Check(bool pass, string label)
    {
        count++;
        if (!pass) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
    private static TaskItem Event(string id, DateTime start, DateTime? end)
    {
        return new TaskItem { Id = id, Title = id, DueAt = start, EndAt = end, HasTime = true };
    }
    public static int Main()
    {
        try
        {
            DateTime day = new DateTime(2026, 10, 26);
            Check(CalendarLogic.LunarShort(day) == "十七", "known lunar date 2026-10-26 is seventeen");
            Check(CalendarLogic.LunarLong(day) == "农历九月十七", "known lunar month September");
            Check(CalendarLogic.LunarShort(new DateTime(2026, 2, 17)) == "正月" && CalendarLogic.LunarLong(new DateTime(2026, 2, 17)) == "农历正月初一", "lunar first day shows month name");
            Check(CalendarLogic.LunarShort(new DateTime(2025, 7, 25)) == "闰六月", "leap lunar month distinguished");
            Check(CalendarLogic.LunarShort(DateTime.MinValue) == "" && CalendarLogic.LunarLong(DateTime.MaxValue) == "", "unsupported lunar dates have empty fallback");
            Check(CalendarLogic.MonthGridStart(day) == new DateTime(2026, 9, 27), "October 2026 grid begins preceding Sunday");
            Check(CalendarLogic.MonthRowCount(day) == 5, "October 2026 has five calendar rows");
            Check(CalendarLogic.MonthRowCount(new DateTime(2026, 2, 1)) == 4, "Sunday-start nonleap February uses four rows");
            Check(CalendarLogic.MonthRowCount(new DateTime(2024, 2, 29)) == 5, "leap February includes day 29");
            Check(CalendarLogic.MonthRowCount(new DateTime(2026, 8, 1)) == 6, "Saturday-start 31-day month uses six rows");

            TaskItem crossing = Event("cross", day.AddHours(23), day.AddDays(1).AddHours(1));
            TaskItem midnightEnd = Event("midnight", day.AddHours(22), day.AddDays(1));
            TaskItem point = Event("no-end", day.AddHours(23).AddMinutes(45), null);
            TaskItem allDay = new TaskItem { Id = "all-day", DueAt = day.AddHours(15), HasTime = false };
            TaskItem completed = Event("completed", day.AddHours(9), day.AddHours(10)); completed.Completed = true;
            TaskItem undated = new TaskItem { Id = "undated", DueAt = null, EndAt = day.AddDays(2) };
            var tasks = new[] { crossing, midnightEnd, point, allDay, completed, undated };
            Check(CalendarLogic.TasksForDay(tasks, day, false).Count == 4 && CalendarLogic.TasksForDay(tasks, day, true).Count == 5, "day query excludes undated and optionally completed tasks");
            Check(CalendarLogic.TasksForDay(tasks, day, false)[0] == allDay, "all-day event sorts before timed events");
            var nextDay = CalendarLogic.TasksForDay(tasks, day.AddDays(1), false);
            Check(nextDay.Count == 1 && nextDay[0] == crossing, "only true cross-midnight interval enters following day");
            Check(CalendarLogic.End(point) == null && point.EndAt == null && CalendarLogic.End(crossing) == crossing.EndAt, "end accessor never invents missing duration");
            TaskItem invalidEnd = Event("bad-end", day.AddHours(12), day.AddHours(11));
            Check(CalendarLogic.End(invalidEnd) == null, "invalid reverse duration treated as unspecified without changing data");
            var daySlots = CalendarLogic.LayoutDay(tasks, day, false);
            Check(daySlots.Count == 3 && daySlots.All(s => s.Task.HasTime), "timeline excludes all-day and completed events");
            CalendarSlot crossSlot = daySlots.Single(s => s.Task == crossing);
            Check(crossSlot.StartMinute == 1380 && crossSlot.EndMinute == 1440 && !crossSlot.ContinuationBefore && crossSlot.ContinuationAfter, "cross-midnight event clips at end of first day");
            crossSlot = CalendarLogic.LayoutDay(tasks, day.AddDays(1), false).Single();
            Check(crossSlot.StartMinute == 0 && crossSlot.EndMinute == 60 && crossSlot.ContinuationBefore && !crossSlot.ContinuationAfter, "cross-midnight continuation clips at beginning of following day");
            CalendarSlot pointSlot = daySlots.Single(s => s.Task == point);
            Check(pointSlot.EndMinute == 1440 && !pointSlot.ContinuationAfter && point.EndAt == null, "missing-end visual block stops at midnight without fabricating continuation");
            var legacy = Event("legacy", day.AddHours(9), null);
            var legacySlot = CalendarLogic.LayoutDay(new[] { legacy }, day, false).Single();
            Check(legacySlot.StartMinute == 540 && legacySlot.EndMinute == 570 && legacy.EndAt == null, "missing-end legacy item receives 30-minute visual-only footprint");

            var a = Event("A", day.AddHours(9), day.AddHours(10));
            var b = Event("B", day.AddHours(9).AddMinutes(30), day.AddHours(11));
            var c = Event("C", day.AddHours(10), day.AddHours(10).AddMinutes(30));
            var d = Event("D", day.AddHours(12), day.AddHours(13));
            var chain = CalendarLogic.LayoutDay(new[] { d, c, b, a }, day, false);
            Check(chain.Single(s => s.Task == a).ColumnCount == 2 && chain.Single(s => s.Task == b).ColumnCount == 2 && chain.Single(s => s.Task == c).ColumnCount == 2, "chained overlap group uses peak concurrency rather than group length");
            Check(chain.Single(s => s.Task == a).Column == chain.Single(s => s.Task == c).Column, "column reused when one event ends exactly at next start");
            Check(chain.Single(s => s.Task == d).ColumnCount == 1 && chain.Single(s => s.Task == d).Column == 0, "disconnected interval restores full column width");
            var e = Event("E", day.AddHours(9).AddMinutes(45), day.AddHours(10).AddMinutes(15));
            var triple = CalendarLogic.LayoutDay(new[] { a, b, c, e }, day, false);
            Check(triple.All(s => s.ColumnCount == 3), "three-way peak overlap propagates across connected component");
            bool collision = false;
            for (int i = 0; i < triple.Count; i++)
                for (int j = i + 1; j < triple.Count; j++)
                    if (triple[i].StartMinute < triple[j].EndMinute && triple[j].StartMinute < triple[i].EndMinute && triple[i].Column == triple[j].Column) collision = true;
            Check(!collision, "overlapping intervals never share a column");
            var shortA = Event("short-A", day.AddHours(14), day.AddHours(14).AddMinutes(5));
            var shortB = Event("short-B", day.AddHours(14).AddMinutes(10), day.AddHours(14).AddMinutes(15));
            var shortSlots = CalendarLogic.LayoutDay(new[] { shortA, shortB }, day, false);
            Check(shortSlots[0].EndMinute == 845 && shortSlots[0].CollisionEndMinute == 870 && shortSlots[0].ColumnCount == 2 && shortSlots[0].Column != shortSlots[1].Column, "short-event visual columns avoid collisions while keeping true endpoints");
            TaskItem wholeDay = Event("full", day.AddDays(-1), day.AddDays(2));
            CalendarSlot full = CalendarLogic.LayoutDay(new[] { wholeDay }, day, false).Single();
            Check(full.StartMinute == 0 && full.EndMinute == 1440 && full.ContinuationBefore && full.ContinuationAfter, "multi-day timed interval spans full intermediate day");
            Check(CalendarLogic.TasksForDay(null, day, false).Count == 0 && CalendarLogic.LayoutDay(null, day, false).Count == 0, "empty source safely returns empty layout");
            Console.WriteLine("ALL " + count + " CALENDAR CHECKS PASSED");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
