using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using Daynote;

public static class SyncTests
{
    static int checks;
    static readonly string Account=Guid.NewGuid().ToString();
    static void Check(bool value,string message) { if(!value)throw new Exception("FAIL: "+message);checks++;Console.WriteLine("PASS: "+message); }
    static void Reject(Action action,string message) { bool failed=false;try{action();}catch{failed=true;}Check(failed,message); }
    static TaskItem Task(string title) { return new TaskItem{Title=title,Notes="Test fixture only",DueAt=new DateTime(2026,11,12,9,30,0),ReminderMinutes=15}; }
    static SyncResponse Response(params SyncItem[] items)
    {
        return new SyncResponse{AccountId=Account,AccountLabel="Fixture account",ServerTime="2026-11-03T12:00:00Z",Items=items.ToList(),Results=new List<SyncResult>()};
    }
    static SyncItem Row(TaskItem task,long version) { return new SyncItem{Id=task.Id,Version=version,Data=SyncTaskData.From(task),UpdatedAt="2026-11-03T12:00:00Z"}; }
    static SyncResponse Ack(SyncChange change,long version,TaskItem task)
    {
        var row=Row(task,version);row.Deleted=change.Deleted;
        var response=Response(row);
        response.Results.Add(new SyncResult{Id=change.Id,MutationId=change.MutationId,Status="applied",Version=version});
        return response;
    }
    static AppData Bound(TaskItem task)
    {
        var source=new AppData();source.Tasks.Add(task);return SyncEngine.Bind(source,Response());
    }
    static AppData Synced(TaskItem task)
    {
        var data=Bound(task);int count;var sent=SyncEngine.Batch(data.Sync.Pending);
        return SyncEngine.Merge(data,sent,Ack(sent[0],1,task),out count);
    }
    static string Json<T>(T value)
    {
        using(var memory=new MemoryStream()) { new DataContractJsonSerializer(typeof(T)).WriteObject(memory,value);return Encoding.UTF8.GetString(memory.ToArray()); }
    }
    public static int Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"Daynote-sync-test-"+Guid.NewGuid().ToString("N"));
        try {
            var untimed=Task("All day");untimed.HasTime=false;untimed.EndAt=null;
            var wire=SyncTaskData.From(untimed);
            Check(wire.DueAt=="2026-11-12" && wire.ToTask(untimed.Id).DueAt==untimed.DueAt.Value.Date,"all-day date has no timezone conversion");
            var unscheduled=Task("No date");unscheduled.DueAt=null;
            string json=Json(SyncTaskData.From(unscheduled));
            Check(json.Contains("\"due_at\":null") && json.Contains("\"end_at\":null") && json.Contains("\"completed\":false"),"wire includes null and false required fields");
            var timed=Task("Timed");var timedWire=SyncTaskData.From(timed);
            Check(timedWire.DueAt.EndsWith("Z") && timedWire.ToTask(timed.Id).DueAt==timed.DueAt,"timed instant roundtrips through UTC");
            timedWire.DueAt="2026-11-12T01:30:00+00:00";
            Check(timedWire.ToTask(timed.Id).DueAt.Value.ToUniversalTime()==new DateTime(2026,11,12,1,30,0,DateTimeKind.Utc),"offset timestamp parses as an instant");
            timedWire.DueAt="2026-11-12T09:30:00";Reject(delegate{timedWire.ToTask(timed.Id);},"timed timestamp without timezone rejected");
            var task=Task("Original");var data=Bound(task);
            Check(data.Version==3 && data.Sync.Pending.Count==1 && data.Sync.Pending[0].BaseVersion==0,"first binding makes durable create request");
            var first=SyncEngine.Clone(data.Sync.Pending[0]);
            data.Tasks[0].Title="Offline revision";SyncEngine.Prepare(data);
            Check(data.Sync.Pending[0].MutationId==first.MutationId && data.Sync.Pending[0].Data.Title=="Original","offline edits never mutate an existing request");
            var store=new DataStore(root);store.Save(data);data=store.Load();
            Check(data.Tasks[0].Title=="Offline revision" && data.Sync.Pending[0].MutationId==first.MutationId,"restart preserves latest desired data and exact pending request atomically");
            int conflicts;
            data=SyncEngine.Merge(data,new List<SyncChange>{first},Ack(first,1,task),out conflicts);
            Check(data.Tasks[0].Title=="Offline revision" && data.Sync.Pending.Count==1 && data.Sync.Pending[0].BaseVersion==1 &&
                data.Sync.Pending[0].MutationId!=first.MutationId,"edit during in-flight create survives and gets next server version");
            var successor=SyncEngine.Clone(data.Sync.Pending[0]);var latest=SyncEngine.Clone(data.Tasks[0]);
            data=SyncEngine.Merge(data,new List<SyncChange>{successor},Ack(successor,2,latest),out conflicts);
            Check(data.Sync.Pending.Count==0 && data.Tasks[0].Title=="Offline revision","acknowledged current content clears queue");
            var replaySource=Synced(task);replaySource.Tasks[0].Title="Sent edit";SyncEngine.Prepare(replaySource);
            var replaySent=SyncEngine.Clone(replaySource.Sync.Pending[0]);
            replaySource.Tasks[0].Title="New local edit after sending";
            var replayCloud=SyncEngine.Clone(task);replayCloud.Title="New remote revision";
            var replayReply=Response(Row(replayCloud,3));
            replayReply.Results.Add(new SyncResult{Id=task.Id,MutationId=replaySent.MutationId,Status="applied",Version=2});
            var replayMerged=SyncEngine.Merge(replaySource,new List<SyncChange>{replaySent},replayReply,out conflicts);
            Check(conflicts==1 && replayMerged.Tasks.Single(t=>t.Id==task.Id).Title=="New remote revision" &&
                replayMerged.Tasks.Any(t=>t.Title=="New local edit after sending（冲突副本）") &&
                replayMerged.Sync.Pending.All(p=>p.Id!=task.Id),
                "old applied replay plus newer remote revision preserves post-send local edit as conflict copy");
            data.Tasks[0].NotifiedAt=DateTime.Now;data.Settings.Width=800;SyncEngine.Prepare(data);
            Check(data.Sync.Pending.Count==0,"notification and window-only changes do not enter cloud queue");
            var pull=Response(Row(data.Tasks[0],2));
            data=SyncEngine.Merge(data,new List<SyncChange>(),pull,out conflicts);
            Check(data.Tasks[0].NotifiedAt.HasValue,"unchanged pull keeps device-local notification state");
            var cloudTime=SyncEngine.Clone(data.Tasks[0]);cloudTime.DueAt=cloudTime.DueAt.Value.AddHours(1);
            data=SyncEngine.Merge(data,new List<SyncChange>(),Response(Row(cloudTime,3)),out conflicts);
            Check(!data.Tasks[0].NotifiedAt.HasValue && data.Sync.Pending.Count==0,"remote timing change rearms local notification without upload echo");

            task=Task("Create then delete");data=Bound(task);first=SyncEngine.Clone(data.Sync.Pending[0]);data.Tasks.Clear();SyncEngine.Prepare(data);
            data=SyncEngine.Merge(data,new List<SyncChange>{first},Ack(first,1,task),out conflicts);
            Check(data.Tasks.Count==0 && data.Sync.Pending.Count==1 && data.Sync.Pending[0].Deleted && data.Sync.Pending[0].BaseVersion==1,"deletion during in-flight creation is queued after acknowledgement");
            first=SyncEngine.Clone(data.Sync.Pending[0]);
            data=SyncEngine.Merge(data,new List<SyncChange>{first},Ack(first,2,task),out conflicts);
            Check(data.Tasks.Count==0 && data.Sync.Pending.Count==0 && data.Sync.Known[0].Deleted,"delete acknowledgement persists tombstone without visible task");
            store.Save(data);Check(store.Load().Sync.Known[0].Deleted,"tombstone survives restart");

            task=Task("Base");data=Synced(task);data.Tasks[0].Title="My edit";SyncEngine.Prepare(data);
            first=SyncEngine.Clone(data.Sync.Pending[0]);
            var cloud=SyncEngine.Clone(task);cloud.Title="Other device edit";
            var conflict=Response(Row(cloud,2));conflict.Results.Add(new SyncResult{Id=task.Id,MutationId=first.MutationId,Status="conflict",Version=2});
            data.Tasks[0].Title="My newest edit";SyncEngine.Prepare(data);
            data=SyncEngine.Merge(data,new List<SyncChange>{first},conflict,out conflicts);
            Check(conflicts==1 && data.Tasks.Count==2 && data.Tasks.Single(t=>t.Id==task.Id).Title=="Other device edit" &&
                data.Tasks.Any(t=>t.Title=="My newest edit（冲突副本）"),"conflict preserves latest in-flight edit as a separate visible copy");
            Check(data.Sync.Pending.Count==1 && data.Sync.Pending[0].BaseVersion==0 && data.Sync.Pending[0].Id!=task.Id,"conflict copy has new identity and create mutation");

            data=Synced(task);data.Tasks.Clear();SyncEngine.Prepare(data);first=SyncEngine.Clone(data.Sync.Pending[0]);
            conflict.Results[0].MutationId=first.MutationId;
            data=SyncEngine.Merge(data,new List<SyncChange>{first},conflict,out conflicts);
            Check(conflicts==1 && data.Tasks.Count==1 && data.Tasks[0].Title==cloud.Title && data.Sync.Pending.Count==0,"delete conflict keeps newer cloud item instead of deleting or duplicating it");

            data=Synced(task);data.Tasks[0].Title="Queued after request began";SyncEngine.Prepare(data);
            var queued=SyncEngine.Clone(data.Sync.Pending[0]);
            data=SyncEngine.Merge(data,new List<SyncChange>(),Response(Row(cloud,2)),out conflicts);
            Check(data.Tasks[0].Title=="Queued after request began" && data.Sync.Pending[0].MutationId==queued.MutationId &&
                data.Sync.Pending[0].BaseVersion==1,"pull preserves a mutation created during the in-flight request");
            var badAccount=Response(Row(cloud,2));badAccount.AccountId=Guid.NewGuid().ToString();
            Reject(delegate{SyncEngine.Merge(data,new List<SyncChange>(),badAccount,out conflicts);},"cross-account response is rejected");
            Reject(delegate{SyncEngine.Bind(data,badAccount);},"bound profile cannot switch accounts");
            Reject(delegate{SyncEngine.Merge(data,new List<SyncChange>(),Response(),out conflicts);},"incomplete cloud snapshot cannot erase existing data");
            Check(data.Tasks[0].Title=="Queued after request began","rejected merges leave source unmodified");

            data=Synced(task);var tombstone=Row(task,2);tombstone.Deleted=true;tombstone.Data=null;
            data=SyncEngine.Merge(data,new List<SyncChange>(),Response(tombstone),out conflicts);
            Check(data.Tasks.Count==0 && data.Sync.Pending.Count==0,"remote tombstone removes only its visible task");
            var imported=SyncEngine.Clone(task);imported.Title="Restored locally";data.Tasks.Add(imported);SyncEngine.Prepare(data);
            Check(data.Sync.Pending.Count==1 && data.Sync.Pending[0].BaseVersion==2 && !data.Sync.Pending[0].Deleted,"explicit restoration references tombstone version");

            var local=Task("Local before binding");var source=new AppData();source.Tasks.Add(local);
            cloud=SyncEngine.Clone(local);cloud.Title="Existing cloud";
            data=SyncEngine.Bind(source,Response(Row(cloud,8)));
            Check(data.Tasks.Count==2 && data.Tasks.Any(t=>t.Title=="Local before binding（冲突副本）") &&
                data.Tasks.Any(t=>t.Title=="Existing cloud"),"first bind preserves colliding local and cloud records");
            Check(source.Sync==null && source.Version==2 && source.Tasks[0].Title=="Local before binding","binding does not mutate original until caller commits");
            data.Tasks[0].ColorId="pink";SyncEngine.Prepare(data);store.Save(data);Check(store.Load().Tasks[0].ColorId=="pink","all protocol colors survive local persistence");
            string exported=File.ReadAllText(store.FilePath);
            Check(!exported.Contains("p_key") && !exported.Contains("sync-key"),"normal calendar export contains no sync credential");
            string secret=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");
            SyncCredentials.Save(root,secret);
            Check(SyncCredentials.Load(root)==secret,"Windows protected credential roundtrips in isolated fixture profile");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root,"sync-key.dpapi"))).Contains(secret),"credential is not written as plaintext");
            SyncCredentials.Delete(root);Check(SyncCredentials.Load(root)==null,"disconnect removes protected credential");

            var pending=new List<SyncChange>();
            for(int i=0;i<130;i++) { var item=Task("Batch "+i);pending.Add(new SyncChange{Id=item.Id,MutationId=Guid.NewGuid().ToString("N"),Data=SyncTaskData.From(item)}); }
            Check(SyncEngine.Batch(pending).Count==100,"batches respect 100 change limit");
            foreach(var change in pending)change.Data.Notes=new string('测',20000);
            var batch=SyncEngine.Batch(pending);
            Check(batch.Count<100 && Encoding.UTF8.GetByteCount(Json(new SyncRequest{Key="fixture",Device=Guid.NewGuid().ToString("N"),Changes=batch}))<900000,"UTF-8 byte budget splits large Chinese notes");
            var invalid=SyncEngine.Clone(data);invalid.Sync.Pending[0].MutationId="invalid";
            string before=File.ReadAllText(store.FilePath);Reject(delegate{store.Save(invalid);},"invalid queue cannot replace saved data");
            Check(File.ReadAllText(store.FilePath)==before,"failed save preserves tasks and queue together");
            Console.WriteLine("PASS: "+checks+" native sync checks.");return 0;
        } catch(Exception error) { Console.Error.WriteLine(error.ToString());return 1; }
        finally {
            string full=Path.GetFullPath(root),parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(full.StartsWith(parent,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("Daynote-sync-test-",StringComparison.Ordinal) && Directory.Exists(full))Directory.Delete(full,true);
        }
    }
}
