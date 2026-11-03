// Optional integration test. Explicitly pass a temporary QA credential file.
// Never run with personal keys. All created records use random fixture IDs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Daynote;

[DataContract]
public sealed class LiveCredential { [DataMember(Name="key")] public string Key; }
[DataContract]
public sealed class LiveCredentialPair
{
    [DataMember(Name="a")] public LiveCredential A;
    [DataMember(Name="b")] public LiveCredential B;
}
public static class SyncLiveTests
{
    static int checks;
    static void Check(bool condition,string name)
    {
        if(!condition)throw new Exception("FAIL: "+name);
        checks++;Console.WriteLine("PASS: "+name);
    }
    static SyncChange Change(TaskItem task,long version,bool deleted)
    {
        return new SyncChange{Id=task.Id,MutationId=Guid.NewGuid().ToString("N"),BaseVersion=version,Deleted=deleted,Data=SyncTaskData.From(task)};
    }
    public static int Main(string[] args)
    {
        if(args.Length!=1) { Console.Error.WriteLine("Pass one explicit temporary QA credentials JSON path.");return 2; }
        var client=new SyncClient();LiveCredentialPair pair;
        using(var input=File.OpenRead(args[0]))pair=(LiveCredentialPair)new DataContractJsonSerializer(typeof(LiveCredentialPair)).ReadObject(input);
        string deviceA=Guid.NewGuid().ToString("N"),deviceOther=Guid.NewGuid().ToString("N");
        var empty=new List<SyncChange>();
        TaskItem task=null;bool createdA=false,createdB=false;
        try {
            var a=client.Exchange(pair.A.Key,deviceA,empty);
            var b=client.Exchange(pair.B.Key,deviceOther,empty);
            Check(a.AccountId!=b.AccountId && a.Results.Count==0 && b.Results.Count==0,"two QA keys validate as distinct accounts without uploads");
            task=new TaskItem{Title="Native sync HTTP fixture",Notes="Temporary automated fixture; no personal schedule.",
                DueAt=new DateTime(2026,11,20,9,0,0),HasTime=true,ReminderMinutes=-1,ColorId="gray"};
            var source=new AppData();source.Tasks.Add(task);
            var bound=SyncEngine.Bind(source,a);
            var create=bound.Sync.Pending.Single(p=>p.Id==task.Id);
            var changes=new List<SyncChange>{create};
            var created=client.Exchange(pair.A.Key,deviceA,changes);createdA=true;
            Check(created.Results.Count==1 && created.Results[0].Status=="applied" && created.Results[0].Version==1,"C# HTTP client uploads a native task with protocol dates and all required fields");
            var repeated=client.Exchange(pair.A.Key,deviceA,changes);
            Check(repeated.Results[0].Version==1 && repeated.Items.Single(i=>i.Id==task.Id).Version==1,"retry of identical mutation is idempotent on server");
            int conflicts;
            var merged=SyncEngine.Merge(bound,changes,created,out conflicts);
            Check(merged.Sync.Pending.All(p=>p.Id!=task.Id) && merged.Tasks.Single(t=>t.Id==task.Id).ColorId=="gray","native merge accepts real server response and clears acknowledged queue");
            var secondDevice=client.Exchange(pair.A.Key,deviceOther,empty);
            Check(secondDevice.Items.Any(i=>i.Id==task.Id && !i.Deleted),"second device pulls first device's new task");
            var isolated=client.Exchange(pair.B.Key,deviceOther,empty);
            Check(!isolated.Items.Any(i=>i.Id==task.Id),"other account cannot read the task");
            var tampered=SyncEngine.Clone(create);tampered.Data.Title="Different body for same mutation";
            bool rejected=false;try{client.Exchange(pair.A.Key,deviceA,new List<SyncChange>{tampered});}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"server rejects mutation ID reuse with different content");

            var remoteTask=SyncEngine.Clone(task);remoteTask.Title="Newer edit from another native device";
            var remoteUpdate=client.Exchange(pair.A.Key,deviceOther,new List<SyncChange>{Change(remoteTask,1,false)});
            Check(remoteUpdate.Results[0].Status=="applied" && remoteUpdate.Results[0].Version==2,"second device updates exact base version");
            var postSend=SyncEngine.Clone(bound);postSend.Tasks.Single(t=>t.Id==task.Id).Title="Local edit made after original send";
            var replay=client.Exchange(pair.A.Key,deviceA,changes);
            var replayMerge=SyncEngine.Merge(postSend,changes,replay,out conflicts);
            Check(conflicts==1 && replayMerge.Tasks.Single(t=>t.Id==task.Id).Title==remoteTask.Title &&
                replayMerge.Tasks.Any(t=>t.Title=="Local edit made after original send（冲突副本）"),
                "real old mutation replay with newer cloud revision preserves local conflict copy");

            merged.Tasks.Single(t=>t.Id==task.Id).Title="Offline edit from first device";SyncEngine.Prepare(merged);
            var stale=merged.Sync.Pending.Single(p=>p.Id==task.Id);
            var staleResponse=client.Exchange(pair.A.Key,deviceA,new List<SyncChange>{stale});
            Check(staleResponse.Results[0].Status=="conflict" && staleResponse.Results[0].Version==2,"stale version returns conflict instead of overwriting remote edit");
            var conflictMerge=SyncEngine.Merge(merged,new List<SyncChange>{stale},staleResponse,out conflicts);
            Check(conflicts==1 && conflictMerge.Tasks.Any(t=>t.Title=="Offline edit from first device（冲突副本）"),"real cloud conflict becomes visible native copy");

            var taskB=SyncEngine.Clone(task);taskB.Title="Independent account B fixture";
            var bCreate=client.Exchange(pair.B.Key,deviceOther,new List<SyncChange>{Change(taskB,0,false)});createdB=true;
            Check(bCreate.Results[0].Status=="applied" && bCreate.Results[0].Version==1,"same record ID belongs to independent account namespaces");
            var aAgain=client.Exchange(pair.A.Key,deviceA,empty);
            Check(aAgain.Items.Single(i=>i.Id==task.Id).Data.Title==remoteTask.Title,"other account write cannot modify first account's record");
            var deletion=client.Exchange(pair.A.Key,deviceA,new List<SyncChange>{Change(remoteTask,2,true)});
            Check(deletion.Results[0].Status=="applied" && deletion.Items.Single(i=>i.Id==task.Id).Deleted,"real delete is persisted as tombstone");
            var pulled=client.Exchange(pair.A.Key,deviceOther,empty);
            Check(pulled.Items.Single(i=>i.Id==task.Id).Deleted,"another device receives deletion tombstone");
            Console.WriteLine("PASS: "+checks+" live native sync checks.");return 0;
        } catch(Exception error) { Console.Error.WriteLine(error.Message);return 1; }
        finally {
            if(task!=null) {
                foreach(var key in new[]{createdA?pair.A.Key:null,createdB?pair.B.Key:null}) {
                    if(key==null)continue;
                    try {
                        var snapshot=client.Exchange(key,deviceA,empty);var row=snapshot.Items.FirstOrDefault(i=>i.Id==task.Id);
                        if(row!=null && !row.Deleted)client.Exchange(key,deviceA,new List<SyncChange>{Change(task,row.Version,true)});
                    } catch { Console.WriteLine("NOTICE: temporary fixture cleanup needs a retry."); }
                }
            }
            pair.A.Key=null;pair.B.Key=null;
        }
    }
}
