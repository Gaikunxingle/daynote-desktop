using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Daynote
{
    [DataContract]
    public class TaskItem
    {
        [DataMember(Order = 0)] public string Id;
        [DataMember(Order = 1)] public string Title;
        [DataMember(Order = 2)] public string Notes;
        [DataMember(Order = 3)] public DateTime? DueAt;
        [DataMember(Order = 4)] public bool HasTime;
        [DataMember(Order = 5)] public int ReminderMinutes;
        [DataMember(Order = 6)] public bool Completed;
        [DataMember(Order = 7)] public DateTime? NotifiedAt;
        [DataMember(Order = 8)] public DateTime? EndAt;
        [DataMember(Order = 9)] public string ColorId;

        public TaskItem() { Defaults(); }
        [OnDeserializing] private void BeforeRead(StreamingContext context) { Defaults(); }
        private void Defaults()
        {
            Id = Guid.NewGuid().ToString("N");
            Title = "";
            Notes = "";
            HasTime = true;
            ReminderMinutes = -1;
            ColorId = "blue";
        }
    }

    [DataContract]
    public class AppSettings
    {
        // Nullable wire values keep the JSON valid while allowing NaN as an unset coordinate.
        [IgnoreDataMember] public double Left;
        [IgnoreDataMember] public double Top;
        [DataMember(Name = "Left", Order = 0)] private double? StoredLeft
        {
            get { return double.IsNaN(Left) || double.IsInfinity(Left) ? (double?)null : Left; }
            set { Left = value ?? double.NaN; }
        }
        [DataMember(Name = "Top", Order = 1)] private double? StoredTop
        {
            get { return double.IsNaN(Top) || double.IsInfinity(Top) ? (double?)null : Top; }
            set { Top = value ?? double.NaN; }
        }
        [DataMember(Order = 2)] public double Width;
        [DataMember(Order = 3)] public double Height;
        [DataMember(Order = 4)] public bool DarkMode;
        [DataMember(Order = 5)] public double Opacity;
        [DataMember(Order = 6)] public bool DesktopMode;
        [DataMember(Order = 7)] public string ThemeMode;
        [DataMember(Order = 8)] public int PlacementVersion;
        [DataMember(Order = 9)] public bool FollowPrimaryTopRight;

        public AppSettings() { Defaults(); }
        [OnDeserializing] private void BeforeRead(StreamingContext context)
        {
            Defaults();
            // Missing placement metadata identifies a pre-2.1 settings document.
            // A freshly constructed settings object already uses the current policy.
            PlacementVersion = 0;
        }
        private void Defaults()
        {
            Left = Top = double.NaN;
            Width = 620;
            Height = 560;
            DarkMode = false;
            Opacity = 1;
            DesktopMode = true;
            ThemeMode = "system";
            PlacementVersion = 1;
            FollowPrimaryTopRight = true;
        }
    }

    [DataContract]
    public class AppData
    {
        [DataMember(Order = 0, IsRequired = true)] public int Version = 2;
        [DataMember(Order = 1, IsRequired = true)] public List<TaskItem> Tasks = new List<TaskItem>();
        [DataMember(Order = 2, IsRequired = true)] public AppSettings Settings = new AppSettings();
    }

    public sealed class DataStore
    {
        private const long MaximumDataBytes = 32 * 1024 * 1024;
        private readonly object gate = new object();
        public string Root { get; private set; }
        public string FilePath { get; private set; }
        public string LoadWarning { get; private set; }
        public string BackupPath { get { return FilePath + ".bak"; } }

        public DataStore(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("数据目录不能为空。", "root");
            Root = Path.GetFullPath(root);
            FilePath = Path.Combine(Root, "reminders.json");
            LoadWarning = "";
        }

        public AppData Load()
        {
            lock (gate)
            {
                Directory.CreateDirectory(Root);
                LoadWarning = "";
                if (!File.Exists(FilePath))
                {
                    if (!File.Exists(BackupPath)) return new AppData();
                    return RecoverBackup("主数据文件不存在");
                }
                try { return ReadValidated(FilePath); }
                catch (Exception ex)
                {
                    if (!IsDataError(ex)) throw new IOException("无法读取提醒数据，请检查文件权限：" + FilePath, ex);
                    string preserved = Preserve(FilePath);
                    return RecoverBackup("主数据文件损坏，原文件已保留为 " + Path.GetFileName(preserved));
                }
            }
        }

        private AppData RecoverBackup(string reason)
        {
            if (File.Exists(BackupPath))
            {
                AppData recovered;
                try { recovered = ReadValidated(BackupPath); }
                catch (Exception ex)
                {
                    if (!IsDataError(ex)) throw new IOException("无法读取备份，请检查文件权限：" + BackupPath, ex);
                    string preserved = Preserve(BackupPath);
                    LoadWarning = reason + "；备份也无法读取，已保留为 " + Path.GetFileName(preserved) + "。当前打开的是空白列表。";
                    return new AppData();
                }
                File.Copy(BackupPath, FilePath, false);
                LoadWarning = reason + "。已恢复最近一次有效备份；最近一次修改可能需要重新录入。";
                return recovered;
            }
            LoadWarning = reason + "。没有可用备份，当前打开的是空白列表。";
            return new AppData();
        }

        public void Save(AppData data)
        {
            lock (gate)
            {
                Validate(data);
                Directory.CreateDirectory(Root);
                string temporary = Path.Combine(Root, ".reminders-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        Serializer().WriteObject(stream, data);
                        if (stream.Length > MaximumDataBytes)
                            throw new IOException("数据文件超过 32 MB；请先导出并清理不再需要的事项。");
                        stream.Flush(true);
                    }
                    // Never rotate a corrupt primary over a healthy backup.
                    if (File.Exists(FilePath))
                    {
                        try { ReadValidated(FilePath); }
                        catch (Exception ex)
                        {
                            if (!IsDataError(ex)) throw;
                            Preserve(FilePath);
                        }
                    }
                    if (File.Exists(FilePath)) File.Replace(temporary, FilePath, BackupPath, true);
                    else File.Move(temporary, FilePath);
                }
                catch (Exception ex)
                {
                    throw new IOException("保存失败，原有数据不会被清空。请检查磁盘空间和目录权限：" + Root, ex);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        try { File.Delete(temporary); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }

        private static DataContractJsonSerializer Serializer() { return new DataContractJsonSerializer(typeof(AppData)); }
        private static AppData ReadValidated(string path)
        {
            AppData result;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaximumDataBytes) throw new SerializationException("数据文件超过安全读取上限。");
                // Some Windows editors add a UTF-8 BOM. The framework JSON reader
                // does not consume it itself; accepting it avoids false corruption.
                if (stream.Length >= 3)
                {
                    bool utf8Bom = stream.ReadByte() == 0xEF && stream.ReadByte() == 0xBB && stream.ReadByte() == 0xBF;
                    if (!utf8Bom) stream.Position = 0;
                }
                result = (AppData)Serializer().ReadObject(stream);
            }
            Validate(result);
            return result;
        }

        private static bool IsDataError(Exception ex)
        {
            return ex is SerializationException || ex is System.Xml.XmlException || ex is FormatException || ex is ArgumentException;
        }

        private static string Preserve(string path)
        {
            string preserved = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            // If preservation fails, fail closed: do not return empty data that may replace the original.
            File.Move(path, preserved);
            return preserved;
        }

        private static void Validate(AppData data)
        {
            if (data == null || (data.Version != 1 && data.Version != 2) || data.Tasks == null || data.Settings == null)
                throw new SerializationException("数据结构或版本不受支持。");
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (TaskItem item in data.Tasks)
            {
                if (item == null || String.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id))
                    throw new SerializationException("事项标识缺失或重复。");
                item.Title = item.Title ?? "";
                item.Notes = item.Notes ?? "";
                if (item.ReminderMinutes < -1 || item.ReminderMinutes > 525600)
                    throw new SerializationException("提醒时间超出支持范围。");
                if (item.ColorId != "blue" && item.ColorId != "purple" && item.ColorId != "green" && item.ColorId != "orange" && item.ColorId != "teal" && item.ColorId != "red")
                    item.ColorId = "blue";
            }
            AppSettings settings = data.Settings;
            // The original document is not rewritten by Load. On the first Save,
            // File.Replace retains the complete version-1 file as the backup.
            // Only window geometry changes; task identities/statuses/dates remain intact.
            if (data.Version == 1)
            {
                settings.PlacementVersion = 0;
                data.Version = 2;
            }
            // User-requested compact layout and primary-screen anchor migrate once.
            // Discard old screen coordinates deliberately: they can point off-screen
            // after switching the primary display. Later explicit choices stay intact.
            if (settings.PlacementVersion < 1)
            {
                settings.Width = 620;
                settings.Height = 560;
                settings.Left = settings.Top = double.NaN;
                settings.FollowPrimaryTopRight = true;
                settings.PlacementVersion = 1;
            }
            if (settings.ThemeMode != "system" && settings.ThemeMode != "light" && settings.ThemeMode != "dark") settings.ThemeMode = "system";
            if (double.IsNaN(settings.Width) || double.IsInfinity(settings.Width) || settings.Width < 560 || settings.Width > 1400) settings.Width = 620;
            if (double.IsNaN(settings.Height) || double.IsInfinity(settings.Height) || settings.Height < 500 || settings.Height > 1200) settings.Height = 560;
            if (double.IsNaN(settings.Opacity) || double.IsInfinity(settings.Opacity) || settings.Opacity < .35 || settings.Opacity > 1) settings.Opacity = 1;
            if (double.IsInfinity(settings.Left)) settings.Left = double.NaN;
            if (double.IsInfinity(settings.Top)) settings.Top = double.NaN;
        }
    }

    public static class ReminderEngine
    {
        private static readonly string[] Weekdays = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

        public static DateTime? EffectiveDue(TaskItem item)
        {
            if (item == null || !item.DueAt.HasValue) return null;
            return item.HasTime ? item.DueAt.Value : item.DueAt.Value.Date.AddHours(9);
        }

        public static bool ShouldNotify(TaskItem item, DateTime now)
        {
            if (item == null || item.Completed || item.NotifiedAt.HasValue || item.ReminderMinutes < 0) return false;
            DateTime? due = EffectiveDue(item);
            if (!due.HasValue) return false;
            DateTime trigger;
            try { trigger = due.Value.AddMinutes(-item.ReminderMinutes); }
            catch (ArgumentOutOfRangeException) { trigger = DateTime.MinValue; }
            return now >= trigger;
        }

        public static string FormatDue(TaskItem item)
        {
            if (item == null || !item.DueAt.HasValue) return "未设日期";
            DateTime due = item.DueAt.Value;
            return due.ToString("yyyy年M月d日", CultureInfo.InvariantCulture) + " " + Weekdays[(int)due.DayOfWeek]
                + " · " + (item.HasTime ? due.ToString("HH:mm", CultureInfo.InvariantCulture) : "全天");
        }
    }
}
