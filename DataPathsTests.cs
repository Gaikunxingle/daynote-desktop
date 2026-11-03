using System;
using System.IO;
using System.Linq;
using System.Text;
using Daynote;

public static class DataPathsTests
{
    private static int count;
    private static string testRoot;
    private static void Check(bool result, string message)
    {
        count++;
        if (!result) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
    private static bool Fails(Action action)
    {
        try { action(); return false; }
        catch (IOException) { return true; }
    }
    private static string Case(string name) { return Path.Combine(testRoot, name); }
    private static string Old(string local) { return Path.Combine(local, "Daynote", "Data"); }
    private static string Virtual(string local, string package) { return Path.Combine(local, "Packages", package, "LocalCache", "Local", "Daynote", "Data"); }
    private static string Primary(string root) { return Path.Combine(root, "reminders.json"); }
    private static byte[] Seed(string root, bool populated, string title)
    {
        var data = new AppData();
        data.Settings.Width = 629;
        data.Settings.Height = 808;
        data.Settings.DeadlinePanelRatio = .33487654320987653;
        if (populated) data.Tasks.Add(new TaskItem { Id = "same-id", Title = title, Notes = "备注\n第二行", IsDeadline = true,
            DueAt = new DateTime(2026, 11, 7), HasTime = false, Completed = false, ColorId = "purple" });
        new DataStore(root).Save(data);
        return File.ReadAllBytes(Primary(root));
    }
    private static void Copy(string source, string root)
    {
        Directory.CreateDirectory(root);
        File.Copy(source, Primary(root));
    }
    public static int Main()
    {
        testRoot = Path.Combine(Path.GetTempPath(), "DnPaths-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(testRoot);
        try
        {
            Check(DataPaths.RootForProfile(testRoot) == Path.Combine(testRoot, "Daynote", "Data"), "default data root is outside AppData");
            foreach (string invalid in new[] { "", "   ", "relative", "C:relative", "\\relative" })
            {
                string value = invalid;
                Check(Fails(delegate { DataPaths.RootForProfile(value); }), "reject missing or relative user profile: " + invalid);
            }

            string local = Path.Combine(Case("prefer-populated"), "Local");
            string destination = Path.Combine(Case("prefer-populated"), "Canonical");
            Seed(Old(local), false, "");
            string packaged = Virtual(local, "Sample.Package_123");
            byte[] original = Seed(packaged, true, "截止事项");
            string message = DataPaths.MigrateLegacyData(destination, local);
            Check(!String.IsNullOrEmpty(message) && DataStore.ReadValidated(Primary(destination)).Tasks.Count == 1, "nonempty package data wins over empty ordinary data");
            Check(original.SequenceEqual(File.ReadAllBytes(Primary(destination))), "migration preserves every source byte and field");
            Check(original.SequenceEqual(File.ReadAllBytes(Primary(packaged))), "source remains byte identical");
            Check(DataStore.ReadValidated(Primary(Old(local))).Tasks.Count == 0, "empty legacy source stays unchanged");
            string[] backups = Directory.GetFiles(Path.Combine(destination, "migration-backups"), "reminders.json", SearchOption.AllDirectories);
            Check(backups.Length == 1 && original.SequenceEqual(File.ReadAllBytes(backups[0])), "migration retains exact backup bytes");
            Check(Directory.GetFiles(Path.Combine(destination, "migration-backups"), "migration.txt", SearchOption.AllDirectories).Length == 1, "migration provenance is recorded");
            Check(Directory.GetFiles(destination, ".migration-*.tmp").Length == 0, "atomic commit leaves no temporary file");
            Check(DataPaths.MigrateLegacyData(destination, "") == "", "existing canonical profile skips legacy path resolution");

            local = Path.Combine(Case("identical"), "Local");
            destination = Path.Combine(Case("identical"), "Canonical");
            original = Seed(Old(local), true, "同一份事项");
            Copy(Primary(Old(local)), Virtual(local, "First_123"));
            Copy(Primary(Old(local)), Virtual(local, "Second_123"));
            DataPaths.MigrateLegacyData(destination, local);
            Check(original.SequenceEqual(File.ReadAllBytes(Primary(destination))), "identical populated candidates are deduplicated");

            local = Path.Combine(Case("conflict"), "Local");
            destination = Path.Combine(Case("conflict"), "Canonical");
            byte[] first = Seed(Old(local), true, "第一份");
            byte[] second = Seed(Virtual(local, "Different_123"), true, "第二份");
            Check(Fails(delegate { DataPaths.MigrateLegacyData(destination, local); }), "different populated candidates fail closed");
            Check(!File.Exists(Primary(destination)), "conflict does not create an empty replacement");
            Check(first.SequenceEqual(File.ReadAllBytes(Primary(Old(local)))) && second.SequenceEqual(File.ReadAllBytes(Primary(Virtual(local, "Different_123")))), "conflicting originals remain unchanged");

            local = Path.Combine(Case("corrupt"), "Local");
            destination = Path.Combine(Case("corrupt"), "Canonical");
            Directory.CreateDirectory(Old(local));
            File.WriteAllText(Primary(Old(local)), "{invalid-json", new UTF8Encoding(false));
            Check(Fails(delegate { DataPaths.MigrateLegacyData(destination, local); }), "corrupt-only source fails closed");
            Check(!File.Exists(Primary(destination)) && File.ReadAllText(Primary(Old(local))) == "{invalid-json", "corrupt source is neither moved nor overwritten");
            Seed(Virtual(local, "Empty_123"), false, "");
            Check(Fails(delegate { DataPaths.MigrateLegacyData(destination, local); }), "empty candidate cannot hide an unreadable older profile");
            original = Seed(Virtual(local, "Healthy_123"), true, "可读事项");
            DataPaths.MigrateLegacyData(destination, local);
            Check(original.SequenceEqual(File.ReadAllBytes(Primary(destination))), "valid populated candidate survives an unrelated corrupt candidate");

            local = Path.Combine(Case("existing"), "Local");
            destination = Path.Combine(Case("existing"), "Canonical");
            original = Seed(destination, false, "");
            Seed(Old(local), true, "旧事项不能复活");
            Check(DataPaths.MigrateLegacyData(destination, local) == "" && original.SequenceEqual(File.ReadAllBytes(Primary(destination))), "intentionally empty canonical profile is never overwritten");
            File.Move(Primary(destination), Primary(destination) + ".bak");
            Check(DataPaths.MigrateLegacyData(destination, local) == "" && !File.Exists(Primary(destination)), "existing canonical backup owns recovery and blocks legacy import");

            local = Path.Combine(Case("fresh"), "Local");
            destination = Path.Combine(Case("fresh"), "Canonical");
            Check(DataPaths.MigrateLegacyData(destination, local) == "" && !File.Exists(Primary(destination)), "fresh install without old data leaves creation to DataStore");

            local = Path.Combine(Case("backup-source"), "Local");
            destination = Path.Combine(Case("backup-source"), "Canonical");
            original = Seed(Old(local), true, "备份中的事项");
            File.Move(Primary(Old(local)), Primary(Old(local)) + ".bak");
            DataPaths.MigrateLegacyData(destination, local);
            Check(original.SequenceEqual(File.ReadAllBytes(Primary(destination))) && File.Exists(Primary(Old(local)) + ".bak"), "missing legacy primary can migrate a valid preserved backup");

            local = Path.Combine(Case("empty-only"), "Local");
            destination = Path.Combine(Case("empty-only"), "Canonical");
            original = Seed(Old(local), false, "");
            DataPaths.MigrateLegacyData(destination, local);
            Check(original.SequenceEqual(File.ReadAllBytes(Primary(destination))), "valid empty legacy profile retains its user settings");
            Console.WriteLine("ALL " + count + " CHECKS PASSED");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string resolved = Path.GetFullPath(testRoot);
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("DnPaths-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside this test's temporary directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
