// Optional physical-device integration harness. Supply disposable QA keys only.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Daynote;

[DataContract] sealed class QACredential { [DataMember(Name="key")] public string Key; }
[DataContract] sealed class QAPair {
    [DataMember(Name="a")] public QACredential A;
    [DataMember(Name="b")] public QACredential B;
}
public static class WindowsRoundTrip {
    static void Require(bool condition,string message) { if(!condition)throw new Exception(message); }
    public static int Main(string[] args) {
        try {
            if(args.Length!=3)throw new Exception("Provide QA credential file, fixture ID and change/check-delete mode.");
            Require(SyncEngine.ValidId(args[1]),"Invalid fixture ID.");
            QAPair pair;
            using(var input=File.OpenRead(args[0]))pair=(QAPair)new DataContractJsonSerializer(typeof(QAPair)).ReadObject(input);
            var client=new SyncClient();var device=Guid.NewGuid().ToString("N");var empty=new List<SyncChange>();
            var response=client.Exchange(pair.A.Key,device,empty);
            Require(response.AccountLabel.StartsWith("Android QA ",StringComparison.Ordinal),"Refusing a non-QA cloud account.");
            var item=response.Items.SingleOrDefault(x=>x.Id==args[1]);Require(item!=null,"Phone-created fixture missing on Windows.");
            if(args[2]=="change") {
                Require(!item.Deleted && item.Data.Title=="联调测试·手机创建","Phone content differs on Windows.");
                Require(!item.Data.HasTime && item.Data.DueAt!=null && item.Data.DueAt.Length==10,"All-day date did not survive Android-to-Windows transfer.");
                var data=SyncEngine.Clone(item.Data); data.Title="联调测试·电脑修改";data.Completed=true;
                var change=new SyncChange { Id=item.Id,MutationId=Guid.NewGuid().ToString("N"),BaseVersion=item.Version,Deleted=false,Data=data };
                var result=client.Exchange(pair.A.Key,device,new List<SyncChange>{change});
                Require(result.Results.Single().Status=="applied","Windows update was not accepted.");
                Console.WriteLine("PASS: Windows fetched the phone-created all-day event and uploaded its edit.");
            } else if(args[2]=="check-delete") {
                Require(item.Deleted,"Phone deletion was not visible on Windows.");
                var other=client.Exchange(pair.B.Key,Guid.NewGuid().ToString("N"),empty);
                Require(other.AccountId!=response.AccountId && !other.Items.Any(x=>x.Id==args[1]),"QA account isolation failed.");
                Console.WriteLine("PASS: Windows observed the phone deletion; the other account remained isolated.");
            } else throw new Exception("Unknown verification mode.");
            return 0;
        } catch(Exception error) { Console.Error.WriteLine(error.Message);return 1; }
    }
}
