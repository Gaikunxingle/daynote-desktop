package app.youmu.daynote;

import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.util.AtomicFile;

import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Writes and response merges run on the main looper; HTTPS runs on one worker. */
public final class CalendarStore {
    public interface Listener { void onChanged(); }
    private static CalendarStore instance;
    public static synchronized CalendarStore get(Context context) {
        if(instance==null)instance=new CalendarStore(context.getApplicationContext());
        return instance;
    }
    private final Handler main=new Handler(Looper.getMainLooper());
    private final ExecutorService network=Executors.newSingleThreadExecutor();
    private final CloudClient cloud=new CloudClient();
    private final AtomicFile file;
    private final AtomicFile backup;
    private SecureKeyStore keyStore;
    private SyncCore.State state=new SyncCore.State();
    private final List<Listener> listeners=new ArrayList<>();
    private final List<Runnable> completions=new ArrayList<>();
    private String key;
    private String status="本机日历";
    private String error;
    private boolean busy;
    private boolean canWrite=true;
    private boolean active;
    private final Runnable scheduledSync=()->syncAsync();
    private final Runnable periodic=new Runnable() {
        @Override public void run() { if(active) { syncAsync(); main.postDelayed(this,60000); } }
    };

    private CalendarStore(Context context) {
        File directory=new File(context.getNoBackupFilesDir(),"daynote-native");
        file=new AtomicFile(new File(directory,"calendar.json"));
        // Do not use calendar.json.bak: AtomicFile reserves that name for its
        // own legacy recovery and would restore it over the latest primary.
        backup=new AtomicFile(new File(directory,"calendar.previous.bak"));
        try {
            if(!directory.isDirectory()&&!directory.mkdirs())throw new IOException("无法创建本机日历目录。");
            if(exists(file)) {
                try { state=read(file); }
                catch(Exception failure) {
                    if(exists(backup)) {
                        try { state=read(backup); error="已从本机备份恢复，请检查最近的日程。"; }
                        catch(Exception recovery) { protectCorrupt(); }
                    } else protectCorrupt();
                }
            } else if(exists(backup)) {
                try { state=read(backup); error="已从本机备份恢复日程。"; }
                catch(Exception recovery) { protectCorrupt(); }
            }
        } catch(Exception failure) { canWrite=false; error="本机日历暂时无法读取，已停止写入以保护原文件。"; }
        // Keystore problems must never prevent offline reading or editing.
        try { keyStore=new SecureKeyStore(context); key=keyStore.load(); }
        catch(Exception failure) { key=null; if(error==null)error="无法读取系统保存的同步码，可继续离线使用，并重新连接原同步码。"; }
        status=key==null?"本机日历":"等待同步";
    }
    private void protectCorrupt() { canWrite=false; error="本机日历和备份均无法读取，已停止写入。请保留应用数据并联系维护者。"; }
    private static boolean exists(AtomicFile file) { return file.getBaseFile().exists()||new File(file.getBaseFile()+".bak").exists(); }
    private static SyncCore.State read(AtomicFile file) throws Exception {
        if(file.getBaseFile().length()>32L*1024*1024)throw new IOException("日历超过读取上限。");
        byte[] bytes=file.readFully(); if(bytes.length>32*1024*1024)throw new IOException("日历超过读取上限。");
        return SyncCore.State.fromJson(new JSONObject(new String(bytes,StandardCharsets.UTF_8)));
    }
    private static void write(AtomicFile file,byte[] bytes) throws IOException {
        FileOutputStream output=null;
        try { output=file.startWrite(); output.write(bytes); file.finishWrite(output); }
        catch(IOException failure) { if(output!=null)file.failWrite(output); throw failure; }
    }
    private synchronized void commit(SyncCore.State next) throws Exception {
        if(!canWrite)throw new IOException("本机日历处于保护状态，暂时不能保存。");
        byte[] encoded=next.toJson().toString().getBytes(StandardCharsets.UTF_8);
        if(encoded.length>32*1024*1024)throw new IOException("本机日历超过容量上限，请先导出备份。");
        if(exists(file)) {
            byte[] validOld=null;
            try { SyncCore.State previous=read(file); validOld=previous.toJson().toString().getBytes(StandardCharsets.UTF_8); }
            catch(Exception invalid) { /* Never overwrite a healthy backup with corrupt bytes. */ }
            if(validOld!=null)write(backup,validOld);
        }
        write(file,encoded); state=next;
    }
    private void requireMain() { if(Looper.myLooper()!=Looper.getMainLooper())throw new IllegalStateException("日历编辑必须在主线程调用。"); }
    public synchronized SyncCore.State snapshot() { return state.copy(); }
    public synchronized List<SyncCore.Record> list() { return state.visible(); }
    public synchronized String status() { return status; }
    public synchronized String error() { return error; }
    public synchronized boolean connected() { return key!=null; }
    public synchronized boolean busy() { return busy; }
    public synchronized boolean canWrite() { return canWrite; }
    public synchronized String accountLabel() { return state.accountLabel; }
    public synchronized String lastSync() { return state.lastSync; }
    public synchronized int pendingCount() { return state.dirtyCount(); }
    public void addListener(Listener listener) {
        if(Looper.myLooper()!=Looper.getMainLooper()) { main.post(()->addListener(listener)); return; }
        if(!listeners.contains(listener))listeners.add(listener);
    }
    public void removeListener(Listener listener) {
        if(Looper.myLooper()!=Looper.getMainLooper()) { main.post(()->removeListener(listener)); return; }
        listeners.remove(listener);
    }
    public void clearError() { requireMain(); synchronized(this) { error=null; } notifyListeners(); }
    private void notifyListeners() { for(Listener listener:new ArrayList<>(listeners))listener.onChanged(); }
    private void fail(String message) { synchronized(this) { error=message; status=message; } notifyListeners(); }

    public boolean save(String id,SyncCore.TaskData data,long expectedRevision) {
        requireMain();
        try {
            SyncCore.TaskData cleaned=data.copy(); cleaned.title=cleaned.title.trim();
            SyncCore.State next=snapshot(); boolean copied=next.edit(id,cleaned,expectedRevision); commit(next);
            synchronized(this) { error=copied?"编辑期间此事项已有更新，你的更改已保留为编辑副本。":null; status=connected()?"已保存，等待同步":"已保存在本机"; }
            changed(); return true;
        } catch(Exception failure) { fail(safeMessage(failure,"本机保存失败，原有日程已保留。")); return false; }
    }
    public boolean save(String id,SyncCore.TaskData data) { return save(id,data,-1); }
    public boolean delete(String id) {
        requireMain();
        try { SyncCore.State next=snapshot(); next.delete(id); commit(next); synchronized(this) { error=null; status="删除已保存，等待同步"; } changed(); return true; }
        catch(Exception failure) { fail(safeMessage(failure,"删除未保存，原有日程已保留。")); return false; }
    }
    public boolean toggle(String id) {
        requireMain(); SyncCore.Record r=snapshot().records.get(id); if(r==null||r.deleted)return false;
        SyncCore.TaskData d=r.data.copy(); d.completed=!d.completed; return save(id,d,r.revision);
    }
    private void changed() { notifyListeners(); scheduleSync(); }
    private void scheduleSync() { main.removeCallbacks(scheduledSync); if(active&&connected())main.postDelayed(scheduledSync,1000); }
    public void setActive(boolean value) {
        if(Looper.myLooper()!=Looper.getMainLooper()) { main.post(()->setActive(value)); return; }
        active=value; main.removeCallbacks(periodic);
        if(value) { syncAsync(); main.postDelayed(periodic,60000); }
        else main.removeCallbacks(scheduledSync);
    }

    /** UI must explain that connecting uploads this profile's local calendar. */
    public void connect(String enteredKey) {
        requireMain(); if(busy()) { fail("正在同步，请稍后再连接。"); return; }
        if(!canWrite()) { fail("本机资料处于保护状态，暂时无法连接。"); return; }
        String proposed=enteredKey==null?"":enteredKey.trim();
        if(!proposed.matches("[A-Za-z0-9_-]{43,128}")) { fail("请粘贴完整的个人同步码。"); return; }
        synchronized(this) { busy=true; error=null; status="正在验证同步码，不会上传日程"; } notifyListeners();
        String device=snapshot().deviceId;
        network.execute(()-> {
            try { SyncCore.Response response=cloud.sync(proposed,device,Collections.emptyList()); main.post(()->finishConnect(proposed,response)); }
            catch(Exception failure) { main.post(()->finishFailure(failure)); }
        });
    }
    private void finishConnect(String proposed,SyncCore.Response response) {
        try {
            SyncCore.Merge merged=SyncCore.merge(snapshot(),response,Collections.emptyList()); commit(merged.state);
            if(keyStore==null)throw new IOException("系统密钥服务暂时不可用，请重新启动应用后连接原同步码。");
            try { keyStore.save(proposed); }
            catch(Exception failure) { throw new IOException("日历已绑定，但系统未能保存同步码。可继续离线使用，请重新连接原同步码。"); }
            synchronized(this) { key=proposed; busy=false; status="已连接："+response.accountLabel;
                error=merged.conflicts>0?"存在同时修改，本地内容已保留为冲突副本。":null; }
            notifyListeners(); finishCompletions(); syncAsync();
        } catch(Exception failure) { finishFailure(failure); }
    }
    public void disconnect() {
        requireMain(); if(busy()) { fail("正在同步，请等待完成后断开。"); return; }
        try {
            if(keyStore!=null)keyStore.clear();
            synchronized(this) { key=null; error=null; status="已断开同步，本机日程和原账号绑定保留"; }
            main.removeCallbacks(scheduledSync); notifyListeners();
        } catch(Exception failure) { fail("无法移除系统保存的同步码，尚未断开。请重试。"); }
    }
    public void syncAsync() { syncAsync(null); }
    public void syncAsync(Runnable completion) {
        if(Looper.myLooper()!=Looper.getMainLooper()) { main.post(()->syncAsync(completion)); return; }
        if(completion!=null)completions.add(completion);
        if(busy())return;
        if(!connected()||!canWrite()) { finishCompletions(); return; }
        if(snapshot().accountId==null) { String saved; synchronized(this) { saved=key; } connect(saved); return; }
        final List<SyncCore.Mutation> batch;
        try { SyncCore.State next=snapshot(); batch=next.prepareBatch(); commit(next); }
        catch(Exception failure) { fail(safeMessage(failure,"本机更改未能保存，尚未发送云端。")); finishCompletions(); return; }
        final String currentKey,device;
        synchronized(this) { busy=true; error=null; status="正在同步"; currentKey=key; device=state.deviceId; }
        notifyListeners();
        network.execute(()-> {
            try { SyncCore.Response response=cloud.sync(currentKey,device,batch); main.post(()->finishSync(response,batch)); }
            catch(Exception failure) { main.post(()->finishFailure(failure)); }
        });
    }
    private void finishSync(SyncCore.Response response,List<SyncCore.Mutation> batch) {
        try {
            // Merge against the current state so edits made while HTTP was running survive.
            SyncCore.Merge merged=SyncCore.merge(snapshot(),response,batch); commit(merged.state);
            synchronized(this) { busy=false; status=state.dirtyCount()==0?"已同步":"已同步，另有 "+state.dirtyCount()+" 项待上传";
                error=merged.conflicts>0?"检测到同时修改，本地编辑已保留为冲突副本；删除冲突保留云端新版。":null; }
            notifyListeners(); finishCompletions(); if(pendingCount()>0)scheduleSync();
        } catch(Exception failure) { finishFailure(failure); }
    }
    private void finishFailure(Exception failure) {
        synchronized(this) { busy=false; error=safeMessage(failure,"暂时无法连接云端，本机更改仍保留。"); status=error; }
        notifyListeners(); finishCompletions();
    }
    private void finishCompletions() {
        List<Runnable> callbacks=new ArrayList<>(completions); completions.clear(); for(Runnable callback:callbacks)callback.run();
    }
    private static String safeMessage(Exception failure,String fallback) {
        if(failure instanceof IllegalArgumentException)return failure.getMessage()==null?fallback:failure.getMessage();
        if(failure instanceof IOException) {
            String message=failure.getMessage();
            // Keep our localized messages; never surface arbitrary network URLs or server bodies.
            if(message!=null&&message.matches(".*[\\u4e00-\\u9fff].*"))return message;
        }
        return fallback;
    }
}
