package app.youmu.daynote;

import android.app.Activity;
import android.app.Instrumentation;
import android.content.Context;
import android.content.Intent;

import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import org.junit.Test;
import org.junit.runner.RunWith;

import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicReference;

import static org.junit.Assert.*;

/** Explicit physical-device QA. No fixture schedules, legacy preferences, cloud credentials, or display changes. */
@RunWith(AndroidJUnit4.class)
public final class DeviceFontIndependenceTest {
    @Test public void independentFontsOnFreshOfflineQaProfile()throws Exception {
        Instrumentation instrumentation=InstrumentationRegistry.getInstrumentation();Context context=instrumentation.getTargetContext();
        assertTrue(BuildConfig.DEBUG);assertEquals("app.youmu.daynote.android.qa",context.getPackageName());
        assertEquals("Select this device test explicitly",DeviceFontIndependenceTest.class.getName(),InstrumentationRegistry.getArguments().getString("class"));
        AtomicReference<CalendarStore> reference=new AtomicReference<>();instrumentation.runOnMainSync(()->reference.set(CalendarStore.get(context)));
        CalendarStore store=reference.get();assertFalse(store.connected());assertNull(store.snapshot().accountId);
        assertTrue("Use a fresh empty QA profile",store.snapshot().records.isEmpty());assertTrue(store.list().isEmpty());
        Activity activity=instrumentation.startActivitySync(new Intent(context,MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        instrumentation.waitForIdleSync();File evidence=new File(context.getExternalFilesDir(null),"font-independence-qa");
        String report=FontIndependenceChecks.run(instrumentation,context,store,activity,evidence);
        assertTrue("Font checks must not add calendar data",store.snapshot().records.isEmpty());assertFalse(store.connected());assertNull(store.snapshot().accountId);
        try(FileOutputStream output=new FileOutputStream(new File(evidence,"font-independence-report.txt"))){output.write(report.getBytes(StandardCharsets.UTF_8));}
        // Keep the real settings window open at App 85% / Widget 150% for manual visual checks.
    }
}
