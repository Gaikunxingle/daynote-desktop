using System;
using System.IO;
using System.Text;
using Daynote;

public static class CoreTests
{
    private static int count;
    private static void Check(bool value, string message)
    {
        count++;
        if (!value) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "Daynote-core-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            DataStore store = new DataStore(root);
            AppData data = store.Load();
            Check(data.Version == 2 && data.Tasks.Count == 0 && data.Settings.DesktopMode && data.Settings.ThemeMode == "system" && data.Settings.Width == 620 && data.Settings.Height == 560 && data.Settings.PlacementVersion == 1 && data.Settings.FollowPrimaryTopRight, "new compact calendar store defaults");
            TaskItem task = new TaskItem { Title = "提交项目报告", Notes = "预算表\n进度说明", DueAt = new DateTime(2026, 10, 27, 18, 0, 0), ReminderMinutes = 30 };
            data.Tasks.Add(task);
            store.Save(data);
            string json = File.ReadAllText(store.FilePath, Encoding.UTF8);
            Check(!json.Contains("NaN") && json.Contains("\"Left\":null"), "unset coordinates are valid JSON null");
            AppData loaded = store.Load();
            Check(loaded.Tasks.Count == 1 && loaded.Tasks[0].Notes == task.Notes && loaded.Tasks[0].DueAt == task.DueAt, "Chinese and date roundtrip");
            Check(double.IsNaN(loaded.Settings.Left), "unset coordinate roundtrip");
            Check(ReminderEngine.FormatDue(task) == "2026年10月27日 周二 · 18:00", "Chinese weekday date formatting");
            DateTime trigger = task.DueAt.Value.AddMinutes(-30);
            Check(!ReminderEngine.ShouldNotify(task, trigger.AddSeconds(-1)), "no notification before lead boundary");
            Check(ReminderEngine.ShouldNotify(task, trigger), "notification at lead boundary");
            Check(ReminderEngine.ShouldNotify(task, trigger.AddDays(2)), "missed reminder catches up on launch");
            task.NotifiedAt = trigger;
            Check(!ReminderEngine.ShouldNotify(task, trigger.AddHours(1)), "persisted notification prevents duplicates");
            task.NotifiedAt = null;
            task.Completed = true;
            Check(!ReminderEngine.ShouldNotify(task, trigger), "completed tasks suppressed");
            task.Completed = false;
            task.ReminderMinutes = -1;
            Check(!ReminderEngine.ShouldNotify(task, trigger), "disabled notifications suppressed");
            task.ReminderMinutes = 0;
            task.HasTime = false;
            Check(ReminderEngine.EffectiveDue(task) == task.DueAt.Value.Date.AddHours(9), "all-day reminder at 09:00");
            task.DueAt = null;
            Check(!ReminderEngine.ShouldNotify(task, trigger), "undated tasks suppressed");

            // Second commit creates the previous valid state as a recovery backup.
            data.Tasks[0].Title = "修改后的标题";
            store.Save(data);
            Check(File.Exists(store.BackupPath), "backup created atomically");
            File.WriteAllText(store.FilePath, "broken-json", Encoding.UTF8);
            loaded = store.Load();
            Check(loaded.Tasks[0].Title == "提交项目报告" && store.LoadWarning.Contains("恢复"), "corrupt primary restores valid previous state");
            Check(Directory.GetFiles(root, "reminders.json.corrupt-*").Length == 1, "corrupt primary retained for recovery");
            Check(store.Load().Tasks.Count == 1 && store.LoadWarning == "", "restored primary remains readable");

            // External corruption must never be rotated over the known-good backup by Save.
            string healthyBackup = File.ReadAllText(store.BackupPath);
            File.WriteAllText(store.FilePath, "external damage");
            store.Save(loaded);
            Check(File.ReadAllText(store.BackupPath) == healthyBackup, "save preserves healthy backup after external corruption");
            Check(Directory.GetFiles(root, "reminders.json.corrupt-*").Length == 2, "save preserves external corruption evidence");

            // Invalid versions cannot silently load as an empty task list.
            File.WriteAllText(store.FilePath, "{\"Version\":99,\"Tasks\":[],\"Settings\":{}}", Encoding.UTF8);
            loaded = store.Load();
            Check(loaded.Tasks.Count == 1 && store.LoadWarning != "", "unsupported schema uses recovery");
            File.WriteAllText(store.FilePath, "broken primary");
            File.WriteAllText(store.BackupPath, "broken backup");
            loaded = store.Load();
            Check(loaded.Tasks.Count == 0 && store.LoadWarning.Contains("备份也无法读取"), "double corruption starts empty with explicit warning");
            Check(Directory.GetFiles(root, "reminders.json.bak.corrupt-*").Length == 1, "corrupt backup retained");
            store.Save(loaded);
            Check(store.Load().Tasks.Count == 0, "new data usable after recovery");

            loaded.Settings.Width = double.NaN;
            loaded.Settings.Height = double.PositiveInfinity;
            loaded.Settings.Opacity = double.NaN;
            loaded.Settings.Left = double.NegativeInfinity;
            loaded.Settings.ThemeMode = "unknown";
            store.Save(loaded);
            var settings = store.Load().Settings;
            Check(settings.Width == 620 && settings.Height == 560 && settings.Opacity == 1, "invalid dimensions and opacity normalize before serialization");
            Check(double.IsNaN(settings.Left) && settings.ThemeMode == "system", "invalid coordinates and theme normalize");
            Check(!File.ReadAllText(store.FilePath).Contains("NaN") && !File.ReadAllText(store.FilePath).Contains("Infinity"), "normalized settings produce finite JSON values");

            TaskItem blockedTask = new TaskItem { Title = "保存失败测试", DueAt = DateTime.Now.AddMinutes(-1), ReminderMinutes = 0 };
            loaded.Tasks.Add(blockedTask);
            store.Save(loaded);
            byte[] beforeFailure = File.ReadAllBytes(store.FilePath);
            bool saveFailed = false;
            blockedTask.NotifiedAt = DateTime.Now;
            using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { store.Save(loaded); }
                catch (IOException) { saveFailed = true; }
            }
            Check(saveFailed, "locked primary reports a failed save");
            Check(Convert.ToBase64String(beforeFailure) == Convert.ToBase64String(File.ReadAllBytes(store.FilePath)), "failed save retains byte-identical primary");
            Check(Directory.GetFiles(root, ".reminders-*.tmp").Length == 0, "failed save cleans staging file");
            var unsavedTask = store.Load().Tasks[0];
            Check(unsavedTask.NotifiedAt == null && ReminderEngine.ShouldNotify(unsavedTask, DateTime.Now), "failed notification-state save remains eligible after restart");
            store.Save(loaded);
            Check(!ReminderEngine.ShouldNotify(store.Load().Tasks[0], DateTime.Now), "retry persists notification state");

            // Saving data that this reader would refuse must not replace a healthy primary.
            beforeFailure = File.ReadAllBytes(store.FilePath);
            loaded.Tasks[0].Notes = new string('x', 32 * 1024 * 1024);
            saveFailed = false;
            try { store.Save(loaded); }
            catch (IOException) { saveFailed = true; }
            Check(saveFailed && Convert.ToBase64String(beforeFailure) == Convert.ToBase64String(File.ReadAllBytes(store.FilePath)), "oversized save cannot replace readable data");
            loaded.Tasks[0].Notes = "";

            // A literal v1 fixture deliberately omits all newly introduced fields.
            string migrationRoot = Path.Combine(root, "migration-v1");
            Directory.CreateDirectory(migrationRoot);
            var migrationStore = new DataStore(migrationRoot);
            string fixture = "{\"Version\":1,\"Tasks\":["
                + "{\"Id\":\"old-timed\",\"Title\":\"旧版已提醒\",\"Notes\":\"不改旧备注\",\"DueAt\":\"\\/Date(1793088000000+0800)\\/\",\"HasTime\":true,\"ReminderMinutes\":30,\"Completed\":false,\"NotifiedAt\":\"\\/Date(1793086200000+0800)\\/\"},"
                + "{\"Id\":\"old-complete\",\"Title\":\"旧版已完成\",\"Notes\":\"保留完成状态\",\"DueAt\":null,\"HasTime\":false,\"ReminderMinutes\":-1,\"Completed\":true,\"NotifiedAt\":null},"
                + "{\"Id\":\"old-all-day\",\"Title\":\"旧版全天\",\"Notes\":\"\",\"DueAt\":\"\\/Date(1793088000000+0800)\\/\",\"HasTime\":false,\"ReminderMinutes\":0,\"Completed\":false,\"NotifiedAt\":null}"
                + "],\"Settings\":{\"Left\":100,\"Top\":80,\"Width\":420,\"Height\":620,\"DarkMode\":true,\"Opacity\":0.8,\"DesktopMode\":false,\"ThemeMode\":\"dark\"}}";
            File.WriteAllText(migrationStore.FilePath, fixture, Encoding.UTF8);
            byte[] originalV1 = File.ReadAllBytes(migrationStore.FilePath);
            var migrated = migrationStore.Load();
            Check(migrated.Version == 2 && migrated.Tasks.Count == 3, "literal UTF-8 BOM v1 fixture migrates all tasks to v2");
            Check(migrated.Tasks[0].Id == "old-timed" && migrated.Tasks[0].Title == "旧版已提醒" && migrated.Tasks[0].Notes == "不改旧备注" && migrated.Tasks[0].ReminderMinutes == 30 && migrated.Tasks[0].NotifiedAt.HasValue && !migrated.Tasks[0].Completed, "v1 migration preserves notification identity and details");
            Check(migrated.Tasks[1].Completed && migrated.Tasks[1].DueAt == null && migrated.Tasks[1].NotifiedAt == null && !migrated.Tasks[2].HasTime && migrated.Tasks[2].ReminderMinutes == 0, "v1 migration preserves completion, no-date and all-day semantics");
            Check(migrated.Tasks[0].EndAt == null && migrated.Tasks[1].EndAt == null && migrated.Tasks[2].EndAt == null && migrated.Tasks[0].ColorId == "blue", "migration never invents event endings");
            Check(migrated.Settings.Width == 620 && migrated.Settings.Height == 560 && double.IsNaN(migrated.Settings.Left) && double.IsNaN(migrated.Settings.Top) && migrated.Settings.PlacementVersion == 1 && migrated.Settings.FollowPrimaryTopRight && migrated.Settings.ThemeMode == "dark" && !migrated.Settings.DesktopMode && migrated.Settings.Opacity == .8, "v1 migration applies compact primary anchor while retaining other preferences");
            Check(Convert.ToBase64String(originalV1) == Convert.ToBase64String(File.ReadAllBytes(migrationStore.FilePath)) && migrationStore.LoadWarning == "", "migration does not alter or quarantine healthy v1 source during load");
            DateTime originalDue = migrated.Tasks[0].DueAt.Value;
            DateTime originalNotified = migrated.Tasks[0].NotifiedAt.Value;
            migrationStore.Save(migrated);
            Check(Convert.ToBase64String(originalV1) == Convert.ToBase64String(File.ReadAllBytes(migrationStore.BackupPath)), "first v2 commit preserves byte-identical v1 backup");
            var reread = migrationStore.Load();
            Check(reread.Tasks[0].DueAt == originalDue && reread.Tasks[0].NotifiedAt == originalNotified && !ReminderEngine.ShouldNotify(reread.Tasks[0], DateTime.MaxValue), "migration roundtrip retains dates and reminder deduplication");
            reread.Tasks[0].EndAt = reread.Tasks[0].DueAt.Value.AddHours(2);
            reread.Tasks[0].ColorId = "purple";
            reread.Tasks[1].ColorId = "invalid";
            reread.Settings.Width = 1100;
            reread.Settings.Height = 900;
            migrationStore.Save(reread);
            var colored = migrationStore.Load();
            Check(colored.Tasks[0].ColorId == "purple" && colored.Tasks[0].EndAt == reread.Tasks[0].EndAt && colored.Tasks[1].ColorId == "blue", "v2 duration and colors persist with fallback for unknown colors");
            Check(colored.Settings.Width == 1100 && colored.Settings.Height == 900, "v2 retains explicit valid calendar size");

            // Literal pre-2.1 v2 settings have no placement metadata; task payloads
            // include v2 color/end fields and must survive the placement migration.
            string placementRoot = Path.Combine(root, "migration-placement-v2");
            Directory.CreateDirectory(placementRoot);
            var placementStore = new DataStore(placementRoot);
            string oldV2 = fixture.Replace("\"Version\":1", "\"Version\":2")
                .Replace("\"Width\":420,\"Height\":620", "\"Width\":860,\"Height\":760")
                .Replace("\"Left\":100,\"Top\":80", "\"Left\":-1900,\"Top\":120")
                .Replace("\"NotifiedAt\":\"\\/Date(1793086200000+0800)\\/\"}", "\"NotifiedAt\":\"\\/Date(1793086200000+0800)\\/\",\"EndAt\":\"\\/Date(1793095200000+0800)\\/\",\"ColorId\":\"teal\"}");
            File.WriteAllText(placementStore.FilePath, oldV2, Encoding.UTF8);
            byte[] oldV2Bytes = File.ReadAllBytes(placementStore.FilePath);
            var placement = placementStore.Load();
            Check(placement.Version == 2 && placement.Settings.PlacementVersion == 1 && placement.Settings.FollowPrimaryTopRight && placement.Settings.Width == 620 && placement.Settings.Height == 560 && double.IsNaN(placement.Settings.Left) && double.IsNaN(placement.Settings.Top), "legacy v2 placement resets size and off-screen coordinates once");
            Check(placement.Tasks.Count == 3 && placement.Tasks[0].Id == "old-timed" && placement.Tasks[1].Id == "old-complete" && placement.Tasks[2].Id == "old-all-day" && placement.Tasks[0].DueAt == originalDue && placement.Tasks[0].NotifiedAt == originalNotified && placement.Tasks[1].Completed && placement.Tasks[0].EndAt == originalDue.AddHours(2) && placement.Tasks[0].ColorId == "teal", "v2 placement migration preserves all task identities dates completion notification and duration");
            Check(placement.Settings.ThemeMode == "dark" && !placement.Settings.DesktopMode && placement.Settings.Opacity == .8, "v2 placement migration retains unrelated preferences");
            Check(Convert.ToBase64String(oldV2Bytes) == Convert.ToBase64String(File.ReadAllBytes(placementStore.FilePath)), "placement migration does not rewrite original during load");
            placementStore.Save(placement);
            Check(Convert.ToBase64String(oldV2Bytes) == Convert.ToBase64String(File.ReadAllBytes(placementStore.BackupPath)), "first placement commit keeps byte-identical old v2 backup");
            placement = placementStore.Load();
            Check(placement.Settings.PlacementVersion == 1 && placement.Settings.FollowPrimaryTopRight && placement.Settings.Width == 620 && placement.Settings.Height == 560, "compact desired size remains stable after save and reload");
            placement.Settings.Width = 760;
            placement.Settings.Height = 650;
            placement.Settings.Left = 155;
            placement.Settings.Top = 95;
            placement.Settings.FollowPrimaryTopRight = false;
            placementStore.Save(placement);
            for (int iteration = 0; iteration < 3; iteration++)
            {
                placement = placementStore.Load();
                placementStore.Save(placement);
            }
            Check(placement.Settings.Width == 760 && placement.Settings.Height == 650 && placement.Settings.Left == 155 && placement.Settings.Top == 95 && !placement.Settings.FollowPrimaryTopRight && placement.Settings.PlacementVersion == 1, "repeated loads retain post-migration desired size and manual placement");
            Check(placement.Tasks[0].DueAt == originalDue && placement.Tasks[0].NotifiedAt == originalNotified && placement.Tasks[1].Completed && placement.Tasks[0].ColorId == "teal", "later placement saves do not modify task schedule or statuses");
            placement.Settings.Width = 560;
            placement.Settings.Height = 500;
            placementStore.Save(placement);
            var minimum = placementStore.Load().Settings;
            Check(minimum.Width == 560 && minimum.Height == 500, "compact minimum dimensions accepted");
            placement.Settings.Width = 559;
            placement.Settings.Height = 499;
            placementStore.Save(placement);
            var tooSmall = placementStore.Load().Settings;
            Check(tooSmall.Width == 620 && tooSmall.Height == 560, "undersized dimensions normalize to compact defaults");
            Console.WriteLine("ALL " + count + " CHECKS PASSED");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            // This is the isolated directory created by this test run, never app/user data.
            string resolved = Path.GetFullPath(root);
            string temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temporaryParent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("Daynote-core-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside this test's temporary directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
