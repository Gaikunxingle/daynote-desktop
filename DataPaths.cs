using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Daynote
{
    // AppData writes can be redirected by an MSIX parent process, even when a
    // child executable itself is unpackaged. Keep the shared data outside that
    // virtualized tree so Explorer and packaged launchers see the same file.
    public static class DataPaths
    {
        public static string DefaultRoot()
        {
            return RootForProfile(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        internal static string RootForProfile(string userProfile)
        {
            return Path.Combine(AbsoluteDirectory(userProfile), "Daynote", "Data");
        }

        // Invoke only for the default profile, after obtaining its instance lock.
        // Explicit --data-dir profiles must never import other profiles' data.
        public static string MigrateLegacyData(string destinationRoot)
        {
            return MigrateLegacyData(destinationRoot,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }

        internal static string MigrateLegacyData(string destinationRoot, string localAppData)
        {
            string destination = AbsoluteDirectory(destinationRoot);
            string primary = Path.Combine(destination, "reminders.json");
            // Even an intentionally empty canonical profile remains authoritative.
            // A canonical backup belongs to DataStore's recovery, not migration.
            if (Exists(primary) || Exists(primary + ".bak")) return "";

            var candidates = new List<Candidate>();
            var failures = new List<string>();
            foreach (string directory in LegacyDirectories(AbsoluteDirectory(localAppData)))
            {
                string file = Path.Combine(directory, "reminders.json");
                Candidate candidate = ReadCandidate(file, failures);
                if (candidate == null) candidate = ReadCandidate(file + ".bak", failures);
                if (candidate != null) candidates.Add(candidate);
            }
            var populated = candidates.Where(c => c.Count > 0).GroupBy(c => c.Hash, StringComparer.Ordinal).Select(g => g.First()).ToList();
            if (populated.Count > 1)
                throw new IOException("发现多份不同的旧日历数据，已保留全部原文件，未自动合并或覆盖。请先核对：\n" +
                    String.Join("\n", populated.Select(c => c.Path).ToArray()));
            if (populated.Count == 0 && failures.Count > 0)
                throw new IOException("旧日历数据无法完整读取，已停止迁移，未创建空白数据或修改原文件：\n" + String.Join("\n", failures.ToArray()));
            Candidate selected = populated.FirstOrDefault() ?? candidates.FirstOrDefault();
            if (selected == null) return "";

            Directory.CreateDirectory(destination);
            // Back up the exact selected bytes and record provenance before the
            // atomic commit. Never rewrite or remove a legacy source.
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
            string backupDirectory = Path.Combine(destination, "migration-backups", stamp);
            Directory.CreateDirectory(backupDirectory);
            WriteDurable(Path.Combine(backupDirectory, "reminders.json"), selected.Bytes);
            string temporary = Path.Combine(destination, ".migration-" + stamp + ".tmp");
            try
            {
                WriteDurable(temporary, selected.Bytes);
                DataStore.ReadValidated(temporary);
                if (!String.Equals(Hash(File.ReadAllBytes(temporary)), selected.Hash, StringComparison.Ordinal))
                    throw new IOException("迁移文件校验失败，原数据未修改。");
                File.WriteAllText(Path.Combine(backupDirectory, "migration.txt"),
                    "utc=" + DateTime.UtcNow.ToString("o") + "\nsource=" + selected.Path + "\ndestination=" + primary +
                    "\nsha256=" + selected.Hash + "\ntasks=" + selected.Count + "\nsourcePreserved=true\n" +
                    (failures.Count == 0 ? "" : "unreadableCandidates=\n" + String.Join("\n", failures.ToArray()) + "\n"), new UTF8Encoding(false));
                // Another launcher may have created the canonical file meanwhile.
                // File.Move never overwrites it, regardless of mutex namespaces.
                if (Exists(primary) || Exists(primary + ".bak")) return "";
                try { File.Move(temporary, primary); }
                catch (IOException) { if (Exists(primary) || Exists(primary + ".bak")) return ""; throw; }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return "已迁移 " + selected.Count + " 条事项到 " + destination + "。旧文件和迁移备份均已保留。";
        }

        private sealed class Candidate
        {
            public string Path, Hash;
            public byte[] Bytes;
            public int Count;
        }

        private static Candidate ReadCandidate(string path, List<string> failures)
        {
            if (!Exists(path)) return null;
            try
            {
                // Hold a read-only handle that denies writes/deletes while both
                // validation and byte capture inspect the very same source file.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    AppData data = DataStore.ReadValidated(path);
                    if (stream.Length > 32 * 1024 * 1024) throw new IOException("数据文件超过读取上限。");
                    var bytes = new byte[(int)stream.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read == 0) throw new EndOfStreamException();
                        offset += read;
                    }
                    return new Candidate { Path = path, Bytes = bytes, Hash = Hash(bytes), Count = data.Tasks.Count };
                }
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) &&
                    !(error is System.Runtime.Serialization.SerializationException) &&
                    !(error is System.Xml.XmlException) && !(error is FormatException) && !(error is ArgumentException)) throw;
                failures.Add(path + " (" + error.Message + ")");
                return null;
            }
        }

        private static IEnumerable<string> LegacyDirectories(string local)
        {
            yield return Path.Combine(local, "Daynote", "Data");
            string packages = Path.Combine(local, "Packages");
            if (!Exists(packages)) yield break;
            foreach (string package in Directory.GetDirectories(packages).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                // Inspect only the known Daynote path within each package.
                string candidate = Path.Combine(package, "LocalCache", "Local", "Daynote", "Data");
                if (Exists(candidate)) yield return candidate;
            }
        }

        private static string AbsoluteDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new IOException("无法确定用户数据目录，已停止打开日历，避免生成另一份空白数据。");
            string root = Path.GetPathRoot(path);
            bool driveAbsolute = root != null && root.Length >= 3 && root[1] == ':' && (root[2] == '\\' || root[2] == '/');
            bool uncAbsolute = root != null && root.StartsWith("\\\\", StringComparison.Ordinal) && root.Trim('\\').Split('\\').Length >= 2;
            if (!Path.IsPathRooted(path) || (!driveAbsolute && !uncAbsolute))
                throw new IOException("数据目录必须是完整绝对路径：" + path);
            return Path.GetFullPath(path);
        }

        private static bool Exists(string path)
        {
            // File.Exists turns access failures into false. Migration must not
            // mistake an unreadable existing profile for a fresh installation.
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        private static void WriteDurable(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
    }
}
