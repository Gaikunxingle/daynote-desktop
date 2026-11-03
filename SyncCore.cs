using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace Daynote
{
    [DataContract]
    public sealed class SyncTaskData
    {
        [DataMember(Name="title")] public string Title;
        [DataMember(Name="notes")] public string Notes;
        [DataMember(Name="due_at")] public string DueAt;
        [DataMember(Name="has_time")] public bool HasTime;
        [DataMember(Name="reminder_minutes")] public int ReminderMinutes;
        [DataMember(Name="completed")] public bool Completed;
        [DataMember(Name="end_at")] public string EndAt;
        [DataMember(Name="color_id")] public string ColorId;
        [DataMember(Name="is_deadline")] public bool IsDeadline;

        public static SyncTaskData From(TaskItem item)
        {
            return new SyncTaskData { Title=item.Title ?? "", Notes=item.Notes ?? "", DueAt=FormatDate(item.DueAt,item.HasTime),
                HasTime=item.HasTime, ReminderMinutes=item.ReminderMinutes, Completed=item.Completed,
                EndAt=FormatDate(item.EndAt,item.HasTime), ColorId=item.ColorId, IsDeadline=item.IsDeadline };
        }
        static string FormatDate(DateTime? value, bool timed)
        {
            if(!value.HasValue)return null;
            if(!timed)return value.Value.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            DateTime date=value.Value;
            if(date.Kind==DateTimeKind.Unspecified)date=DateTime.SpecifyKind(date,DateTimeKind.Local);
            return date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture);
        }
        static DateTime? ParseDate(string value,bool timed)
        {
            if(value==null)return null;
            DateTime date;
            if(!timed) {
                if(!DateTime.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date))
                    throw new SerializationException("云端日期格式无效。");
                return date;
            }
            DateTimeOffset offset;
            if(!Regex.IsMatch(value,@"T.*(Z|[+-]\d{2}:\d{2})$") ||
                !DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.None,out offset))
                throw new SerializationException("云端时刻缺少有效时区。");
            return offset.LocalDateTime;
        }
        public TaskItem ToTask(string id)
        {
            if(Title==null || Notes==null || ReminderMinutes < -1 || ReminderMinutes>525600 ||
                !new[]{"blue","teal","purple","orange","green","red","pink","gray"}.Contains(ColorId))
                throw new SerializationException("云端事项内容无效。");
            return new TaskItem { Id=id,Title=Title,Notes=Notes,DueAt=ParseDate(DueAt,HasTime),HasTime=HasTime,
                ReminderMinutes=ReminderMinutes,Completed=Completed,EndAt=ParseDate(EndAt,HasTime),ColorId=ColorId,IsDeadline=IsDeadline };
        }
        public static bool Same(SyncTaskData a,SyncTaskData b)
        {
            if(a==null || b==null)return a==b;
            return a.Title==b.Title && a.Notes==b.Notes && a.DueAt==b.DueAt && a.HasTime==b.HasTime &&
                a.ReminderMinutes==b.ReminderMinutes && a.Completed==b.Completed && a.EndAt==b.EndAt &&
                a.ColorId==b.ColorId && a.IsDeadline==b.IsDeadline;
        }
    }
    [DataContract]
    public sealed class SyncChange
    {
        [DataMember(Name="id")] public string Id;
        [DataMember(Name="mutation_id")] public string MutationId;
        [DataMember(Name="base_version")] public long BaseVersion;
        [DataMember(Name="deleted")] public bool Deleted;
        [DataMember(Name="data")] public SyncTaskData Data;
    }
    [DataContract]
    public sealed class SyncItem
    {
        [DataMember(Name="id")] public string Id;
        [DataMember(Name="version")] public long Version;
        [DataMember(Name="deleted")] public bool Deleted;
        [DataMember(Name="data")] public SyncTaskData Data;
        [DataMember(Name="updated_at")] public string UpdatedAt;
    }
    [DataContract]
    public sealed class SyncResult
    {
        [DataMember(Name="id")] public string Id;
        [DataMember(Name="mutation_id")] public string MutationId;
        [DataMember(Name="status")] public string Status;
        [DataMember(Name="version")] public long Version;
    }
    [DataContract]
    public sealed class SyncRequest
    {
        [DataMember(Name="p_key")] public string Key;
        [DataMember(Name="p_device")] public string Device;
        [DataMember(Name="p_changes")] public List<SyncChange> Changes;
    }
    [DataContract]
    public sealed class SyncResponse
    {
        [DataMember(Name="account_id")] public string AccountId;
        [DataMember(Name="account_label")] public string AccountLabel;
        [DataMember(Name="server_time")] public string ServerTime;
        [DataMember(Name="results")] public List<SyncResult> Results;
        [DataMember(Name="items")] public List<SyncItem> Items;
    }
    [DataContract]
    public sealed class SyncState
    {
        [DataMember] public string AccountId;
        [DataMember] public string AccountLabel;
        [DataMember] public string DeviceId;
        [DataMember] public string LastSyncUtc;
        [DataMember] public List<SyncItem> Known = new List<SyncItem>();
        [DataMember] public List<SyncChange> Pending = new List<SyncChange>();
    }
    public static class SyncEngine
    {
        public static T Clone<T>(T source)
        {
            var serializer=new DataContractJsonSerializer(typeof(T));
            using(var stream=new MemoryStream()) { serializer.WriteObject(stream,source);stream.Position=0;return (T)serializer.ReadObject(stream); }
        }
        public static bool ValidId(string value) { return value!=null && Regex.IsMatch(value,@"\A[0-9a-f]{32}\z"); }
        public static List<SyncChange> Batch(List<SyncChange> pending)
        {
            var batch=new List<SyncChange>();long bytes=2048;
            var serializer=new DataContractJsonSerializer(typeof(SyncChange));
            foreach(var change in pending) {
                long size;
                using(var stream=new MemoryStream()) { serializer.WriteObject(stream,change);size=stream.Length+1; }
                if(bytes+size>850000) {
                    if(batch.Count==0)throw new InvalidOperationException("单条事项内容过长，暂时无法同步；本机内容已保留。");
                    break;
                }
                batch.Add(Clone(change));bytes+=size;if(batch.Count==100)break;
            }
            return batch;
        }
        public static void ValidateState(SyncState state)
        {
            if(state==null)return;
            Guid account;
            if(!Guid.TryParse(state.AccountId,out account) || !ValidId(state.DeviceId) || state.Known==null || state.Pending==null)
                throw new SerializationException("同步资料结构无效。");
            var ids=new HashSet<string>();
            foreach(var row in state.Known) {
                if(row==null || !ValidId(row.Id) || row.Version<1 || !ids.Add(row.Id) || (!row.Deleted && row.Data==null))
                    throw new SerializationException("同步记录结构无效。");
                if(!row.Deleted)row.Data.ToTask(row.Id);
            }
            ids.Clear();var mutations=new HashSet<string>();
            foreach(var change in state.Pending) {
                if(change==null || !ValidId(change.Id) || !ValidId(change.MutationId) || change.BaseVersion<0 ||
                    !ids.Add(change.Id) || !mutations.Add(change.MutationId) || (!change.Deleted && change.Data==null))
                    throw new SerializationException("待同步记录结构无效。");
                if(!change.Deleted)change.Data.ToTask(change.Id);
            }
        }
        public static void ValidateResponse(SyncResponse response)
        {
            Guid account;
            if(response==null || !Guid.TryParse(response.AccountId,out account) || response.Items==null ||
                response.Results==null || response.Items.Count>10000)
                throw new SerializationException("同步服务返回了无效数据，本机日程未更改。");
            var ids=new HashSet<string>();
            foreach(var row in response.Items) {
                if(row==null || !ValidId(row.Id) || row.Version<1 || !ids.Add(row.Id) || (!row.Deleted && row.Data==null))
                    throw new SerializationException("同步服务返回了无效事项，本机日程未更改。");
                if(!row.Deleted)row.Data=SyncTaskData.From(row.Data.ToTask(row.Id));
            }
        }
        // Existing pending requests are immutable. A later local edit stays in Tasks;
        // after the old request is acknowledged, Prepare creates its successor.
        public static void Prepare(AppData data)
        {
            if(data.Sync==null)return;
            var state=data.Sync;ValidateState(state);
            var pending=new List<SyncChange>(state.Pending);
            var busy=new HashSet<string>(pending.Select(p=>p.Id));
            var known=state.Known.ToDictionary(k=>k.Id);
            var local=data.Tasks.ToDictionary(t=>t.Id);
            foreach(var task in data.Tasks) {
                if(!ValidId(task.Id))throw new SerializationException("同步事项标识无效。");
                if(busy.Contains(task.Id))continue;
                SyncItem row;known.TryGetValue(task.Id,out row);
                var content=SyncTaskData.From(task);
                if(row!=null && !row.Deleted && SyncTaskData.Same(content,row.Data))continue;
                pending.Add(new SyncChange{Id=task.Id,MutationId=Guid.NewGuid().ToString("N"),BaseVersion=row==null?0:row.Version,Deleted=false,Data=content});
            }
            foreach(var row in state.Known)
                if(!row.Deleted && !local.ContainsKey(row.Id) && !busy.Contains(row.Id))
                    pending.Add(new SyncChange{Id=row.Id,MutationId=Guid.NewGuid().ToString("N"),BaseVersion=row.Version,Deleted=true,Data=row.Data});
            state.Pending=pending;
        }
        static bool SameDesired(TaskItem task,SyncChange sent)
        {
            return sent.Deleted ? task==null : task!=null && SyncTaskData.Same(SyncTaskData.From(task),sent.Data);
        }
        static void ApplyRemote(AppData data,SyncItem row)
        {
            TaskItem current=data.Tasks.FirstOrDefault(t=>t.Id==row.Id);
            if(row.Deleted) { if(current!=null)data.Tasks.Remove(current);return; }
            var replacement=row.Data.ToTask(row.Id);
            if(current!=null && Nullable.Equals(current.DueAt,replacement.DueAt) && current.HasTime==replacement.HasTime &&
                current.ReminderMinutes==replacement.ReminderMinutes && current.IsDeadline==replacement.IsDeadline)
                replacement.NotifiedAt=current.NotifiedAt;
            if(current==null)data.Tasks.Add(replacement);
            else data.Tasks[data.Tasks.IndexOf(current)]=replacement;
        }
        static void PreserveConflict(AppData data,TaskItem current)
        {
            if(current==null)return;
            var copy=Clone(current);copy.Id=Guid.NewGuid().ToString("N");
            string title=copy.Title ?? "";copy.Title=(title.Length>190?title.Substring(0,190):title)+"（冲突副本）";
            data.Tasks.Add(copy);
        }
        public static AppData Bind(AppData source,SyncResponse response)
        {
            ValidateResponse(response);
            if(source.Sync!=null && source.Sync.AccountId!=response.AccountId)
                throw new InvalidOperationException("此资料已绑定另一账号。请使用单独的资料目录，不能混用两人的日程。");
            if(response.Results.Count!=0)throw new SerializationException("首次连接必须为空变更验证。");
            var data=Clone(source);
            if(data.Sync!=null) {
                int ignored;return Merge(data,new List<SyncChange>(),response,out ignored);
            }
            foreach(var task in data.Tasks)if(!ValidId(task.Id))task.Id=Guid.NewGuid().ToString("N");
            data.Version=3;
            data.Sync=new SyncState{AccountId=response.AccountId,AccountLabel=response.AccountLabel,DeviceId=Guid.NewGuid().ToString("N"),LastSyncUtc=response.ServerTime};
            foreach(var row in response.Items) {
                var current=data.Tasks.FirstOrDefault(t=>t.Id==row.Id);
                if(current!=null && (row.Deleted || !SyncTaskData.Same(SyncTaskData.From(current),row.Data)))
                    PreserveConflict(data,current);
                ApplyRemote(data,row);
            }
            data.Sync.Known=Clone(response.Items);Prepare(data);return data;
        }
        public static AppData Merge(AppData source,List<SyncChange> sent,SyncResponse response,out int conflicts)
        {
            ValidateResponse(response);ValidateState(source.Sync);
            if(source.Sync==null || source.Sync.AccountId!=response.AccountId)
                throw new InvalidOperationException("同步账号不匹配，本机日程未更改。");
            var remote=response.Items.ToDictionary(r=>r.Id);
            foreach(var old in source.Sync.Known)if(!remote.ContainsKey(old.Id))
                throw new SerializationException("云端快照缺少已知事项，本机日程未更改。");
            var results=new Dictionary<string,SyncResult>();
            foreach(var result in response.Results) {
                if(result==null || !ValidId(result.Id) || !ValidId(result.MutationId) || result.Version<0 ||
                    (result.Status!="applied" && result.Status!="conflict") || results.ContainsKey(result.MutationId))
                    throw new SerializationException("同步确认格式无效。");
                results.Add(result.MutationId,result);
            }
            if(results.Count!=sent.Count)throw new SerializationException("同步确认数量不完整，保留待发送记录。");
            var sentById=new Dictionary<string,SyncChange>();
            foreach(var change in sent) {
                SyncResult result;SyncItem row;
                if(!results.TryGetValue(change.MutationId,out result) || result.Id!=change.Id ||
                    (result.Version>0 && (!remote.TryGetValue(change.Id,out row) || row.Version<result.Version)) ||
                    (result.Status=="applied" && result.Version<1))
                    throw new SerializationException("同步确认与请求不一致，保留待发送记录。");
                sentById.Add(change.Id,change);
            }
            var data=Clone(source);conflicts=0;
            var pending=source.Sync.Pending.ToDictionary(p=>p.Id);
            var processed=new HashSet<string>();
            foreach(var row in response.Items) {
                SyncChange change;var current=data.Tasks.FirstOrDefault(t=>t.Id==row.Id);
                if(sentById.TryGetValue(row.Id,out change)) {
                    var result=results[change.MutationId];
                    if(result.Status=="conflict") { PreserveConflict(data,current);ApplyRemote(data,row);conflicts++; }
                    else if(SameDesired(current,change))ApplyRemote(data,row);
                    else if(row.Version>result.Version) {
                        // An idempotent replay can acknowledge an older revision while
                        // returning a newer edit from another device. Do not rebase a
                        // post-send local edit onto a remote revision the user never saw.
                        PreserveConflict(data,current);ApplyRemote(data,row);conflicts++;
                    }
                    processed.Add(row.Id);
                } else if(!pending.ContainsKey(row.Id))ApplyRemote(data,row);
            }
            foreach(var change in sent)if(!processed.Contains(change.Id)) {
                var result=results[change.MutationId];
                if(result.Status!="conflict" || result.Version!=0)throw new SerializationException("云端确认缺少对应事项。");
                var current=data.Tasks.FirstOrDefault(t=>t.Id==change.Id);
                PreserveConflict(data,current);if(current!=null)data.Tasks.Remove(current);conflicts++;
            }
            var acknowledged=new HashSet<string>(sent.Select(c=>c.MutationId));
            data.Sync.Pending=data.Sync.Pending.Where(p=>!acknowledged.Contains(p.MutationId)).ToList();
            data.Sync.Known=Clone(response.Items);data.Sync.AccountLabel=response.AccountLabel;
            data.Sync.LastSyncUtc=response.ServerTime;
            Prepare(data);return data;
        }
    }
}
