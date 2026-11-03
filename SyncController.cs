using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Daynote
{
    public sealed partial class AppController
    {
        readonly SyncClient syncClient=new SyncClient();
        string syncKey;
        string syncMessage="尚未连接云同步。";
        bool syncBusy;
        bool syncSetup;
        DateTime nextSync=DateTime.MaxValue;
        SyncResponse deferredSync;
        List<SyncChange> deferredSent;
        public bool SyncBusy { get { return syncBusy; } }
        public bool SyncConnected { get { return !String.IsNullOrEmpty(syncKey); } }
        public string SyncDescription {
            get {
                string account=Data.Sync==null?"本机日历，尚未绑定账号":("账号："+(Data.Sync.AccountLabel ?? "个人日历"));
                int pending=Data.Sync==null?0:Data.Sync.Pending.Count;
                string last="尚无同步记录";DateTimeOffset date;
                if(Data.Sync!=null && DateTimeOffset.TryParse(Data.Sync.LastSyncUtc,CultureInfo.InvariantCulture,DateTimeStyles.None,out date))
                    last="上次同步："+date.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture);
                return account+"\n"+syncMessage+"\n"+last+"\n待发送："+pending+" 条";
            }
        }
        void InitializeSync()
        {
            if(Program.Preview) { syncMessage="演示模式不会连接云端。";return; }
            if(Data.Sync==null)return;
            try {
                syncKey=SyncCredentials.Load(Store.Root);
                syncMessage=SyncConnected?"已连接，等待同步。":"同步已断开；账号绑定与待发送更改仍保留。";
            } catch { syncMessage="无法读取此电脑保存的密钥，请重新连接原账号。"; }
        }
        void ScheduleSyncAfterSave()
        {
            if(SyncConnected && Data.Sync!=null && Data.Sync.Pending.Count>0) {
                DateTime when=DateTime.UtcNow.AddSeconds(2);if(nextSync>when)nextSync=when;
            }
        }
        public void RequestSync()
        {
            if(!SyncConnected || Program.Preview || exiting)return;
            nextSync=DateTime.UtcNow;
            SyncTick();
        }
        void SyncTick()
        {
            if(exiting)return;
            if(deferredSync!=null && !editing) {
                var response=deferredSync;var sent=deferredSent;deferredSync=null;deferredSent=null;
                ApplySyncResponse(sent,response);
            }
            if(syncBusy || syncSetup || editing || !SyncConnected || Program.Preview || Data.Sync==null || DateTime.UtcNow<nextSync)return;
            List<SyncChange> batch;
            try {
                // Save the exact mutation IDs before any network request.
                if(!Save(false)) { syncMessage="本机保存失败，暂未发送云同步。";nextSync=DateTime.UtcNow.AddMinutes(1);return; }
                batch=SyncEngine.Batch(Data.Sync.Pending);
            } catch(Exception error) { syncMessage=error.Message;nextSync=DateTime.UtcNow.AddMinutes(1);return; }
            syncBusy=true;syncMessage="正在同步…";nextSync=DateTime.UtcNow.AddMinutes(1);
            string key=syncKey,device=Data.Sync.DeviceId;
            RunExchange(key,device,batch,delegate(SyncResponse response,Exception error) {
                if(error!=null) { syncBusy=false;syncMessage=error.Message;nextSync=DateTime.UtcNow.AddMinutes(1);return; }
                if(editing) { deferredSync=response;deferredSent=batch;syncMessage="已收到更新，等待编辑完成…";return; }
                ApplySyncResponse(batch,response);
            });
        }
        void RunExchange(string key,string device,List<SyncChange> changes,Action<SyncResponse,Exception> done)
        {
            Task.Factory.StartNew(delegate { return syncClient.Exchange(key,device,changes); }).ContinueWith(delegate(Task<SyncResponse> task) {
                if(app.Dispatcher.HasShutdownStarted)return;
                try { app.Dispatcher.BeginInvoke(new Action(delegate {
                    if(exiting)return;
                    if(task.IsFaulted)done(null,task.Exception.GetBaseException());
                    else if(task.IsCanceled)done(null,new InvalidOperationException("同步已取消。"));
                    else done(task.Result,null);
                })); } catch(InvalidOperationException) { }
            });
        }
        static bool SameReminder(TaskItem a,TaskItem b)
        {
            return Nullable.Equals(a.DueAt,b.DueAt) && a.HasTime==b.HasTime &&
                a.ReminderMinutes==b.ReminderMinutes && a.IsDeadline==b.IsDeadline;
        }
        void AdoptSyncData(AppData candidate)
        {
            foreach(var old in Data.Tasks) {
                var replacement=candidate.Tasks.FirstOrDefault(t=>t.Id==old.Id);
                if(replacement==null || !SameReminder(old,replacement))notifiedThisRun.Remove(old.Id);
            }
            Data=candidate;
            if(window!=null)window.Render();
        }
        void ApplySyncResponse(List<SyncChange> sent,SyncResponse response)
        {
            try {
                int conflicts;
                var candidate=SyncEngine.Merge(Data,sent,response,out conflicts);
                Store.Save(candidate);
                AdoptSyncData(candidate);
                syncMessage=conflicts==0?"同步完成。":"同步完成；冲突修改已保留为副本，删除冲突保留了云端事项。";
                if(conflicts>0 && tray!=null)tray.ShowBalloonTip(7000,"朝夕同步","检测到同时修改。请检查“冲突副本”；删除冲突保留了云端事项。",System.Windows.Forms.ToolTipIcon.Info);
                nextSync=DateTime.UtcNow.AddSeconds(Data.Sync.Pending.Count>0?2:60);
            } catch(Exception error) {
                syncMessage="同步未合并："+error.Message;
                nextSync=DateTime.UtcNow.AddMinutes(1);
            } finally { syncBusy=false; }
        }
        public void ShowSyncSettings()
        {
            if(editing) { MessageBox.Show("请先完成当前日程的编辑，再打开账号与同步。","朝夕同步",MessageBoxButton.OK,MessageBoxImage.Information);return; }
            var dialog=new SyncSettingsWindow(this,Dark);
            dialog.ShowDialog();
        }
        public void ValidateSyncKey(string key,Action<SyncResponse,string> done)
        {
            if(editing) { done(null,"请先完成当前日程的编辑，再连接同步。");return; }
            if(Program.Preview) { done(null,"演示模式不连接云端。");return; }
            if(syncBusy) { done(null,"正在同步，请稍后重试。");return; }
            if(String.IsNullOrWhiteSpace(key) || key.Trim().Length<32 || key.Trim().Length>256) { done(null,"请填写完整的个人同步密钥。");return; }
            syncBusy=true;syncMessage="正在验证账号，不会上传日程…";
            string device=Data.Sync==null?Guid.NewGuid().ToString("N"):Data.Sync.DeviceId;
            RunExchange(key.Trim(),device,new List<SyncChange>(),delegate(SyncResponse response,Exception error) {
                syncBusy=false;
                if(error!=null) { syncMessage=error.Message;done(null,error.Message);return; }
                if(Data.Sync!=null && Data.Sync.AccountId!=response.AccountId) {
                    syncMessage="该密钥属于另一账号，未连接、未上传。";
                    done(null,"此资料已绑定另一账号。两人的日历必须使用各自的资料目录；不能用另一人的密钥替换。");return;
                }
                syncMessage="账号验证完成，等待确认。";
                syncSetup=true;
                try { done(response,null); } finally { syncSetup=false; }
            });
        }
        public string ConnectSync(string key,SyncResponse response)
        {
            if(editing)return "请先完成当前日程的编辑，再连接同步。";
            if(syncBusy)return "正在同步，请稍后重试。";
            try {
                bool first=Data.Sync==null;
                if(first && File.Exists(Store.FilePath)) {
                    string backup=Path.Combine(Store.Root,"before-cloud-"+DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N").Substring(0,8)+".json");
                    File.Copy(Store.FilePath,backup,false);
                }
                var candidate=SyncEngine.Bind(Data,response);
                Store.Save(candidate);AdoptSyncData(candidate);
                try { SyncCredentials.Save(Store.Root,key.Trim()); }
                catch {
                    syncKey=null;syncMessage="资料已绑定，但无法保存密钥。请重新连接同一账号。";
                    return syncMessage;
                }
                syncKey=key.Trim();syncMessage="已连接，正在安排同步。";RequestSync();return null;
            } catch(Exception error) { syncMessage=error.Message;return error.Message; }
        }
        public string DisconnectSync()
        {
            if(syncBusy)return "正在同步，请等待完成后断开。";
            try {
                SyncCredentials.Delete(Store.Root);syncKey=null;nextSync=DateTime.MaxValue;
                syncMessage="已断开。日程、待发送更改及原账号绑定保留在本机。";return null;
            } catch { return "无法移除保存的密钥，尚未断开，请重试。"; }
        }
        void StopSync() { nextSync=DateTime.MaxValue;syncClient.Cancel();syncKey=null; }
    }
}
