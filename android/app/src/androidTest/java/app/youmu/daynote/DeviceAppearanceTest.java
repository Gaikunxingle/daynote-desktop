package app.youmu.daynote;

import android.app.Instrumentation;
import android.content.Context;
import android.content.Intent;

import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;

import org.junit.Test;
import org.junit.runner.RunWith;

import java.time.LocalDate;
import java.time.ZoneId;
import java.util.concurrent.atomic.AtomicReference;

import static org.junit.Assert.*;

/** Explicit opt-in fixture for manual device QA. Not selected by the CI workflow. */
@RunWith(AndroidJUnit4.class)
public final class DeviceAppearanceTest {
    @Test public void seedOfflineAppearanceFixture() {
        Instrumentation instrumentation=InstrumentationRegistry.getInstrumentation();
        Context context=instrumentation.getTargetContext();
        assertEquals("Run this fixture only by its explicit test class",DeviceAppearanceTest.class.getName(),
                InstrumentationRegistry.getArguments().getString("class"));
        assertTrue("Never seed a release app",BuildConfig.DEBUG);
        assertEquals("Never seed the real calendar package","app.youmu.daynote.android.qa",context.getPackageName());
        AtomicReference<CalendarStore> reference=new AtomicReference<>();
        instrumentation.runOnMainSync(()->reference.set(CalendarStore.get(context)));
        CalendarStore store=reference.get();
        assertFalse("Never seed a connected account",store.connected());
        assertNull("Never seed a previously bound account",store.snapshot().accountId);
        assertTrue("Use a fresh, empty QA profile",store.list().isEmpty());
        assertTrue("Do not overwrite an existing local history",store.snapshot().records.isEmpty());
        instrumentation.runOnMainSync(()->{
            assertTrue(DisplayPreferences.setTheme(context,"system"));
            assertTrue(DisplayPreferences.setFontScale(context,1f));
            String[] titles={"整理阅读笔记（虚构测试）","散步与休息（虚构测试）","检查桌面小组件（虚构测试）"};
            String[] colors={"blue","teal","orange"};
            for(int i=0;i<titles.length;i++) {
                SyncCore.TaskData data=new SyncCore.TaskData();
                data.title=titles[i];data.notes="仅用于测试版界面检查，不代表真实日程。";
                data.dueAt=LocalDate.now().atTime(9+i*3,0).atZone(ZoneId.systemDefault()).toInstant().toString();
                data.hasTime=true;data.completed=false;data.reminderMinutes=-1;data.colorId=colors[i];
                assertTrue("Could not save the fictional fixture",store.save(null,data,-1));
            }
        });
        assertEquals("Only three fictional fixtures should exist",3,store.list().size());
        assertTrue(store.list().stream().allMatch(r->!r.deleted&&!r.data.completed&&r.data.reminderMinutes==-1
                &&LocalDate.now().equals(r.data.dueDate())&&r.data.title.endsWith("（虚构测试）")));
        assertFalse(store.connected());assertNull(store.snapshot().accountId);
        instrumentation.startActivitySync(new Intent(context,MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        instrumentation.waitForIdleSync();
        // Leave the native activity open for manual checks; no display, rotation, or system settings changes.
    }
}
