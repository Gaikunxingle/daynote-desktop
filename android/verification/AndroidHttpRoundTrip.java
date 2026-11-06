package app.youmu.daynote;

import org.json.JSONObject;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.time.LocalDate;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/**
 * Exercises the production Android HTTP and sync classes on a desktop JVM.
 * This is not an Android-device, Keystore, notification, or UI test.
 * Usage: AndroidHttpRoundTrip <repository-root> create|pull-delete
 * Uses only the temporary .private/android-qa-credentials.json account a.
 */
public final class AndroidHttpRoundTrip {
    private static final String PHONE_TITLE="联调测试·手机创建";
    private static final String WINDOWS_TITLE="联调测试·电脑修改";
    private final Path repository;
    private final Path stateFile;
    private final Path idFile;
    private final String key;
    private final CloudClient cloud=new CloudClient();

    private AndroidHttpRoundTrip(Path repository) throws Exception {
        this.repository=repository.toAbsolutePath().normalize();
        stateFile=this.repository.resolve("qa/android-http-state.json");
        idFile=this.repository.resolve("qa/android-http-fixture-id.txt");
        Path credential=this.repository.resolve(".private/android-qa-credentials.json");
        if(!Files.isRegularFile(credential)||Files.size(credential)>16384)
            throw new IllegalStateException("Temporary Android QA credentials are missing or malformed.");
        String loaded;
        try {
            JSONObject config=new JSONObject(Files.readString(credential,StandardCharsets.UTF_8));
            loaded=config.getJSONObject("a").getString("key");
        } catch(Exception invalid) {
            // JSON parser failures can include input text; do not propagate them.
            throw new IllegalStateException("Temporary Android QA credentials cannot be parsed.");
        }
        if(!loaded.matches("[A-Za-z0-9_-]{43,128}"))
            throw new IllegalStateException("Temporary QA pairing credential has an invalid shape.");
        key=loaded;
    }
    private static void require(boolean condition,String message) {
        if(!condition)throw new IllegalStateException(message);
    }
    private SyncCore.Response exchange(SyncCore.State state,List<SyncCore.Mutation> changes) throws Exception {
        SyncCore.Response response=cloud.sync(key,state.deviceId,changes);
        require(response.accountLabel!=null&&response.accountLabel.startsWith("Android QA"),
                "Refusing to use an account without the Android QA label.");
        return response;
    }
    private static void atomicWrite(Path path,String content) throws Exception {
        Files.createDirectories(path.getParent()); Path temporary=path.resolveSibling(path.getFileName()+".tmp");
        Files.writeString(temporary,content,StandardCharsets.UTF_8);
        Files.move(temporary,path,StandardCopyOption.ATOMIC_MOVE,StandardCopyOption.REPLACE_EXISTING);
    }
    private void persist(SyncCore.State state) throws Exception { atomicWrite(stateFile,state.toJson().toString()); }
    private SyncCore.State upload(SyncCore.State current) throws Exception {
        List<SyncCore.Mutation> sent=current.prepareBatch();
        persist(current); // Exact mutation IDs survive a lost network response.
        SyncCore.Response response=exchange(current,sent);
        SyncCore.Merge merged=SyncCore.merge(current,response,sent);
        require(merged.conflicts==0,"QA operation unexpectedly conflicted.");
        persist(merged.state); return merged.state;
    }
    private void create() throws Exception {
        SyncCore.State state=new SyncCore.State();
        // Always authenticate with an empty handshake before sending any event.
        state=SyncCore.merge(state,exchange(state,Collections.emptyList()),Collections.emptyList()).state;
        Set<String> before=new HashSet<>(state.records.keySet());
        SyncCore.TaskData fixture=new SyncCore.TaskData(); fixture.title=PHONE_TITLE;
        fixture.notes="独立 Android QA 账号中的虚构跨端联调事项。";
        fixture.dueAt=LocalDate.now().toString(); fixture.hasTime=false; fixture.reminderMinutes=-1;
        state.edit(null,fixture,-1);
        String id=null; for(String candidate:state.records.keySet())if(!before.contains(candidate))id=candidate;
        require(id!=null,"Production new-event path did not generate a fixture ID.");
        atomicWrite(idFile,id+System.lineSeparator());
        state=upload(state); SyncCore.Record row=state.records.get(id);
        require(row!=null&&!row.deleted&&!row.dirty&&row.version>=1&&PHONE_TITLE.equals(row.data.title),
                "Android production HTTP create was not acknowledged.");
        System.out.println("PASS: Android production Java HTTP/core created and persisted the QA event (desktop JVM).");
        System.out.println("Fixture ID saved to qa/android-http-fixture-id.txt.");
    }
    private void pullDelete() throws Exception {
        require(Files.isRegularFile(stateFile)&&Files.isRegularFile(idFile),"Run the create phase first.");
        String id=Files.readString(idFile,StandardCharsets.UTF_8).trim();
        require(id.matches("[0-9a-f]{32}"),"Saved QA fixture ID is invalid.");
        SyncCore.State state=SyncCore.State.fromJson(new JSONObject(Files.readString(stateFile,StandardCharsets.UTF_8)));
        require(state.accountId!=null,"QA state is not bound to an account.");
        // Validate credentials/account and pull the genuine Windows-client edit.
        state=SyncCore.merge(state,exchange(state,Collections.emptyList()),Collections.emptyList()).state;
        SyncCore.Record row=state.records.get(id);
        require(row!=null&&!row.deleted&&row.data.completed&&WINDOWS_TITLE.equals(row.data.title),
                "Android production HTTP/core did not receive the Windows edit.");
        long version=row.version; persist(state);
        System.out.println("PASS: Android production Java HTTP/core received the Windows title and completion state (desktop JVM).");
        state.delete(id); state=upload(state); row=state.records.get(id);
        require(row!=null&&row.deleted&&!row.dirty&&row.version>version,"Phone-side deletion did not become a newer cloud tombstone.");
        System.out.println("PASS: Android production Java HTTP/core uploaded a versioned deletion (desktop JVM).");
    }
    public static void main(String[] arguments) {
        try {
            require(arguments.length==2,"Usage: AndroidHttpRoundTrip <repository-root> create|pull-delete");
            AndroidHttpRoundTrip test=new AndroidHttpRoundTrip(Path.of(arguments[0]));
            if("create".equals(arguments[1]))test.create();
            else if("pull-delete".equals(arguments[1]))test.pullDelete();
            else throw new IllegalStateException("Unknown QA phase.");
        } catch(Exception error) {
            // No stack trace, request, credentials object, or arbitrary JSON body.
            String message=error instanceof IllegalStateException||error instanceof java.io.IOException?error.getMessage():null;
            System.err.println("FAIL: "+(message==null?"QA verification failed; private input was not logged.":message));
            System.exit(1);
        }
    }
}
