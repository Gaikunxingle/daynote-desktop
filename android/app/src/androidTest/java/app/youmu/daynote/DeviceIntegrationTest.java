package app.youmu.daynote;

import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.os.SystemClock;
import android.util.AtomicFile;

import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;

import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;

import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.time.LocalDate;
import java.util.Arrays;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

import static org.junit.Assert.*;

/**
 * Three explicit phases against a disposable QA account. Run each method
 * independently; the host changes the event using the Windows client between
 * phase 1 and phase 2, and force-stops the QA app to exercise cold-start recovery.
 * The fixture configuration is injected into this QA app's private files dir.
 * Never use a real person's pairing key. No key or config content is logged.
 */
@RunWith(AndroidJUnit4.class)
public final class DeviceIntegrationTest {
    private static final String QA_PACKAGE="app.youmu.daynote.android.qa";
    private static final String PHONE_TITLE="联调测试·手机创建";
    private static final String WINDOWS_TITLE="联调测试·电脑修改";
    private static final long TIMEOUT_MILLIS=60000;

    private static final class Config {
        final Context context;
        final File file;
        final JSONObject json;
        final String key;
        Config(Context context,File file,JSONObject json,String key) {
            this.context=context; this.file=file; this.json=json; this.key=key;
        }
        String fixtureId() throws Exception {
            String id=json.getString("fixture_id");
            assertTrue("QA fixture identifier must be a 32-character hex ID",id.matches("[0-9a-f]{32}"));
            return id;
        }
    }

    private Config config() throws Exception {
        Context context=InstrumentationRegistry.getInstrumentation().getTargetContext();
        assertTrue("Integration tests require a debug build",BuildConfig.DEBUG);
        assertEquals("Integration tests must never target the release app",QA_PACKAGE,context.getPackageName());
        assertFalse("QA calendar must not enter Android automatic backups",
                (context.getApplicationInfo().flags & ApplicationInfo.FLAG_ALLOW_BACKUP)!=0);
        File file=new File(context.getFilesDir(),"qa-config.json");
        assertEquals("Unexpected QA configuration location",context.getFilesDir().getCanonicalFile(),file.getCanonicalFile().getParentFile());
        assertTrue("Host must inject the temporary QA configuration first",file.isFile());
        assertTrue("QA configuration is unexpectedly large",file.length()<8192);
        byte[] bytes=new AtomicFile(file).readFully();
        JSONObject json;
        try { json=new JSONObject(new String(bytes,StandardCharsets.UTF_8)); }
        catch(Exception malformed) { throw new AssertionError("QA configuration must contain valid JSON"); }
        finally { Arrays.fill(bytes,(byte)0); }
        String key=json.getString("key");
        assertTrue("QA pairing key has an invalid shape",key.matches("[A-Za-z0-9_-]{43,128}"));
        Config result=new Config(context,file,json,key); result.fixtureId(); return result;
    }
    private CalendarStore store(Context context) {
        AtomicReference<CalendarStore> reference=new AtomicReference<>();
        main(()->reference.set(CalendarStore.get(context))); return reference.get();
    }
    private void main(Runnable operation) { InstrumentationRegistry.getInstrumentation().runOnMainSync(operation); }
    private long deadline() { return SystemClock.elapsedRealtime()+TIMEOUT_MILLIS; }
    private void waitIdle(CalendarStore store,long deadline) {
        AtomicBoolean idle=new AtomicBoolean();
        do {
            // A main-looper barrier avoids observing the brief busy=false state
            // between pairing completion and its immediately-started first sync.
            main(()->idle.set(!store.busy()));
            if(idle.get())break;
            SystemClock.sleep(100);
        } while(SystemClock.elapsedRealtime()<deadline);
        assertTrue("Timed out waiting for the QA sync operation",idle.get());
        assertNull("QA sync reported an error: "+store.error(),store.error());
    }
    private void sync(CalendarStore store,long deadline) throws Exception {
        CountDownLatch completed=new CountDownLatch(1);
        main(()->store.syncAsync(completed::countDown));
        long remaining=Math.max(1,deadline-SystemClock.elapsedRealtime());
        assertTrue("Timed out waiting for the QA sync callback",completed.await(remaining,TimeUnit.MILLISECONDS));
        waitIdle(store,deadline);
    }
    private void assertStoredKey(Config config) throws Exception {
        String restored=new SecureKeyStore(config.context).load();
        assertNotNull("Pairing key was not restored from Android Keystore",restored);
        byte[] expected=config.key.getBytes(StandardCharsets.UTF_8),actual=restored.getBytes(StandardCharsets.UTF_8);
        try {
            // assertEquals would print both secrets on failure. Compare bytes
            // without ever attaching either value to the assertion message.
            assertTrue("Stored pairing key differs from the injected QA credential",MessageDigest.isEqual(expected,actual));
        } finally { Arrays.fill(expected,(byte)0); Arrays.fill(actual,(byte)0); }
    }
    private SyncCore.State diskState(Config config) throws Exception {
        File directory=new File(config.context.getNoBackupFilesDir(),"daynote-native");
        File primary=new File(directory,"calendar.json");
        assertTrue("Calendar state was not persisted",primary.isFile()&&primary.length()>0);
        assertTrue("Calendar state must remain in the no-backup directory",
                primary.getCanonicalPath().startsWith(config.context.getNoBackupFilesDir().getCanonicalPath()+File.separator));
        byte[] bytes=new AtomicFile(primary).readFully();
        return SyncCore.State.fromJson(new JSONObject(new String(bytes,StandardCharsets.UTF_8)));
    }
    private void saveGeneratedFixtureId(Config config,String id) throws Exception {
        config.json.put("fixture_id",id);
        byte[] bytes=config.json.toString().getBytes(StandardCharsets.UTF_8);
        AtomicFile file=new AtomicFile(config.file); FileOutputStream output=null;
        try { output=file.startWrite(); output.write(bytes); file.finishWrite(output); }
        catch(Exception error) { if(output!=null)file.failWrite(output); throw error; }
        finally { Arrays.fill(bytes,(byte)0); }
    }

    @Test public void test01PhoneCreates() throws Exception {
        long deadline=deadline(); Config config=config(); CalendarStore store=store(config.context);
        assertTrue("The first phase requires an empty disposable QA calendar",store.list().isEmpty());
        main(()->store.connect(config.key));
        waitIdle(store,deadline);
        assertTrue("QA account was not connected",store.connected());
        assertNotNull("QA account binding was not persisted",store.snapshot().accountId);
        assertStoredKey(config);

        SyncCore.TaskData fixture=new SyncCore.TaskData(); fixture.title=PHONE_TITLE;
        fixture.notes="仅用于手机与电脑双向同步联调的虚构事项。";
        fixture.hasTime=false; fixture.dueAt=LocalDate.now().toString(); fixture.reminderMinutes=-1;
        AtomicBoolean saved=new AtomicBoolean(); main(()->saved.set(store.save(null,fixture,-1)));
        assertTrue("Phone creation was not saved: "+store.error(),saved.get());
        assertEquals("Unexpected number of QA events",1,store.list().size());
        SyncCore.Record created=store.list().get(0);
        assertEquals(PHONE_TITLE,created.data.title);
        // Use the actual application's new-event path, then expose only the
        // generated ID to the host via the already-private QA config file.
        saveGeneratedFixtureId(config,created.id);
        sync(store,deadline);
        SyncCore.Record synced=store.snapshot().records.get(created.id);
        assertNotNull("Created event disappeared after sync",synced);
        assertTrue("Created event was not acknowledged by the server",synced.version>=1);
        assertFalse("Created event remains dirty after sync",synced.dirty);
        assertFalse("Created event unexpectedly became deleted",synced.deleted);
        SyncCore.Record persisted=diskState(config).records.get(created.id);
        assertNotNull("Created event is missing from disk",persisted);
        assertEquals(synced.version,persisted.version);
        assertEquals(PHONE_TITLE,persisted.data.title);
    }

    @Test public void test02ReadsWindowsChange() throws Exception {
        long deadline=deadline(); Config config=config(); String id=config.fixtureId();
        CalendarStore store=store(config.context);
        // The host force-stops the QA app between phases. These assertions must
        // pass before reconnect(), proving persisted key and profile recovery.
        assertTrue("Cold start did not restore the QA credential",store.connected());
        assertStoredKey(config);
        assertNotNull("Cold start did not restore the locally created event",store.snapshot().records.get(id));
        sync(store,deadline);
        SyncCore.Record changed=store.snapshot().records.get(id);
        assertNotNull("Windows-edited fixture is missing",changed);
        assertEquals(WINDOWS_TITLE,changed.data.title);
        assertTrue("Windows completion state did not reach the phone",changed.data.completed);
        assertTrue("Windows edit did not advance the cloud version",changed.version>=2);
        assertFalse("Downloaded fixture should be clean",changed.dirty);
        SyncCore.Record persisted=diskState(config).records.get(id);
        assertEquals(WINDOWS_TITLE,persisted.data.title);
        assertTrue(persisted.data.completed);
        main(()->AgendaWidgetProvider.refreshAll(config.context));
    }

    @Test public void test03PhoneDeletes() throws Exception {
        long deadline=deadline(); Config config=config(); String id=config.fixtureId();
        CalendarStore store=store(config.context);
        assertTrue("Cold start did not restore the QA credential",store.connected());
        sync(store,deadline);
        SyncCore.Record before=store.snapshot().records.get(id);
        assertNotNull("Deletion fixture is missing",before);
        assertEquals("Run the Windows edit and phase 2 before deletion",WINDOWS_TITLE,before.data.title);
        assertFalse("Deletion fixture has already been deleted",before.deleted);
        long previousVersion=before.version;
        AtomicBoolean deleted=new AtomicBoolean(); main(()->deleted.set(store.delete(id)));
        assertTrue("Phone deletion was not saved: "+store.error(),deleted.get());
        sync(store,deadline);
        SyncCore.Record tombstone=store.snapshot().records.get(id);
        assertNotNull("Deletion must retain a versioned tombstone",tombstone);
        assertTrue("Deletion was not acknowledged by the cloud",tombstone.deleted&&tombstone.version>previousVersion);
        assertFalse("Acknowledged deletion remains dirty",tombstone.dirty);
        assertTrue("Deleted fixture is still visible",store.list().stream().noneMatch(r->r.id.equals(id)));
        SyncCore.Record persisted=diskState(config).records.get(id);
        assertTrue("Deletion tombstone was not persisted",persisted.deleted);
        assertEquals(tombstone.version,persisted.version);
        main(()->AgendaWidgetProvider.refreshAll(config.context));
    }
}
