package app.youmu.daynote;

import android.app.Activity;
import android.app.Application;
import android.app.Instrumentation;
import android.app.UiAutomation;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.Rect;
import android.os.Bundle;
import android.os.ParcelFileDescriptor;
import android.os.SystemClock;
import android.view.View;
import android.view.ViewGroup;
import android.view.accessibility.AccessibilityNodeInfo;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.RemoteViews;
import android.widget.TextView;

import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;

import org.junit.Test;
import org.junit.runner.RunWith;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.lang.reflect.Constructor;
import java.lang.reflect.Method;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.BooleanSupplier;
import java.util.function.Predicate;

import static org.junit.Assert.*;

/** Runs only in a freshly cleared, offline QA app on an emulator. Never uses pairing credentials. */
@RunWith(AndroidJUnit4.class)
public final class AppearanceCompletionTest {
    private static final String QA_PACKAGE="app.youmu.daynote.android.qa";
    private static final String CIRCLE_TITLE="完成按钮检查（虚构）";
    private static final String WIDGET_TITLE="小组件完成检查（虚构）";
    private static final String DRAFT_NOTES="外观变化后仍应保存到同一条虚构日程。";
    private final Instrumentation instrumentation=InstrumentationRegistry.getInstrumentation();
    private final UiAutomation automation=instrumentation.getUiAutomation();
    private final AtomicReference<Activity> resumed=new AtomicReference<>();
    private final StringBuilder report=new StringBuilder();
    private Context context;
    private CalendarStore store;
    private File artifacts;
    private Application application;
    private final Application.ActivityLifecycleCallbacks lifecycle=new Application.ActivityLifecycleCallbacks() {
        @Override public void onActivityResumed(Activity activity){if(activity instanceof MainActivity)resumed.set(activity);}
        @Override public void onActivityCreated(Activity a,Bundle b){}
        @Override public void onActivityStarted(Activity a){}
        @Override public void onActivityPaused(Activity a){}
        @Override public void onActivityStopped(Activity a){}
        @Override public void onActivitySaveInstanceState(Activity a,Bundle b){}
        @Override public void onActivityDestroyed(Activity a){}
    };

    @Test public void appearancePersistsAndCompletionRetainsHistory() throws Exception {
        context=instrumentation.getTargetContext();
        assertTrue("Requires a debug build",BuildConfig.DEBUG);
        assertEquals("Never run fixtures in the real app",QA_PACKAGE,context.getPackageName());
        assertEquals("Never change a physical device's display","1",shell("getprop ro.kernel.qemu").trim());
        artifacts=new File(context.getExternalFilesDir(null),"appearance-qa");
        assertTrue(artifacts.isDirectory()||artifacts.mkdirs());
        application=(Application)context.getApplicationContext();
        main(()->application.registerActivityLifecycleCallbacks(lifecycle));
        try {
            AtomicReference<CalendarStore> reference=new AtomicReference<>();
            main(()->reference.set(CalendarStore.get(context)));store=reference.get();
            assertOffline();assertTrue("Use a fresh QA app for these fixtures",store.list().isEmpty());
            shell("cmd uimode night no");resize(2280,1980);
            String circleId=seed(CIRCLE_TITLE,9),widgetId=seed(WIDGET_TITLE,10);
            instrumentation.startActivitySync(new Intent(context,MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            waitFor(()->resumed.get()!=null,"MainActivity did not resume");idle();
            float originalSize=headerTextSize();

            openSettings();chooseTheme("浅色","light");chooseFont("特大（150%）",1.5f);closeSettings();
            assertFalse(DisplayPreferences.isDark(current()));assertHeaderContrast(false);
            float enlargedSize=headerTextSize();
            assertTrue("Font setting did not enlarge the actual header",enlargedSize>originalSize*1.2f);
            screenshot("01-wide-light-150.png");resize(1080,2400);screenshot("02-compact-light-150.png");
            Activity before=current();main(before::recreate);awaitRecreated(before);idle();
            assertEquals("light",DisplayPreferences.theme(context));assertEquals(1.5f,DisplayPreferences.fontScale(context),.001f);
            assertEquals("Actual text size changed after recreation",enlargedSize,headerTextSize(),.75f);
            assertHeaderContrast(false);report.append("PASS font 150% and light theme persist through activity recreation; actual header enlarged\n");

            resize(2280,1980);openSettings();chooseTheme("深色","dark");closeSettings();
            assertHeaderContrast(true);screenshot("03-wide-dark-150.png");
            resize(1080,2400);screenshot("04-compact-dark-150.png");
            before=current();main(before::recreate);awaitRecreated(before);idle();
            assertEquals("dark",DisplayPreferences.theme(context));assertHeaderContrast(true);
            assertEquals(enlargedSize,headerTextSize(),.75f);
            report.append("PASS dark theme persists; native screenshots captured at compact and wide widths\n");

            resize(2280,1980);openSettings();chooseTheme("跟随系统","system");closeSettings();
            shell("cmd uimode night yes");waitFor(()->DisplayPreferences.isDark(current())&&headerMatchesTheme(true),"System dark mode was not followed");idle();assertHeaderContrast(true);
            shell("cmd uimode night no");waitFor(()->!DisplayPreferences.isDark(current())&&headerMatchesTheme(false),"System light mode was not followed");idle();assertHeaderContrast(false);
            assertEquals("system",DisplayPreferences.theme(context));report.append("PASS follow-system theme responds to both system modes\n");

            openSettings();chooseFont("标准（100%）",1f);closeSettings();
            clickActivity(v->v.getContentDescription()!=null&&("完成："+CIRCLE_TITLE).contentEquals(v.getContentDescription()));
            waitFor(()->record(circleId).data.completed,"Circle did not complete the fixture");idle();
            assertFalse("Completed item remains in Today",hasActivityText(CIRCLE_TITLE));
            clickFilter("全部");assertFalse("Completed item remains in All pending",hasActivityText(CIRCLE_TITLE));
            clickFilter("已完成");assertTrue("Completed history lost the item",hasActivityText(CIRCLE_TITLE));
            assertFalse("Completion must not delete the record",record(circleId).deleted);screenshot("05-completed-history.png");
            clickActivity(v->v.getContentDescription()!=null&&("恢复待办："+CIRCLE_TITLE).contentEquals(v.getContentDescription()));
            waitFor(()->!record(circleId).data.completed,"Restore did not recover the item");idle();
            assertFalse("Restored item remains in Completed",hasActivityText(CIRCLE_TITLE));
            clickFilter("当天");assertTrue("Restored item did not return to Today",hasActivityText(CIRCLE_TITLE));
            report.append("PASS circle removes item from Today/All, retains Completed history, and restore returns it to Today\n");

            // Use the public display preferences to model configuration changes while an editor is open.
            // The settings UI itself was exercised above; this checks the independent draft's identity.
            main(()->current().startActivity(new Intent(context,MainActivity.class).setAction(MainActivity.ACTION_OPEN)
                    .putExtra(MainActivity.EXTRA_TASK_ID,widgetId).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP)));
            awaitText("编辑日程");setNotes(DRAFT_NOTES);
            before=current();Activity editing=before;
            main(()->{DisplayPreferences.setFontScale(context,1.3f);DisplayPreferences.setTheme(context,"dark");editing.recreate();});
            awaitRecreated(before);awaitText("编辑日程");awaitText(DRAFT_NOTES);screenshot("06-editor-after-appearance-recreate.png");
            clickText("保存日程");idle();
            assertEquals("Appearance recreation must update the same record",2,store.list().size());
            assertEquals(DRAFT_NOTES,record(widgetId).data.notes);
            report.append("PASS existing editor draft survives font/theme recreation and saves under its original record id\n");

            long initialRevision=record(widgetId).revision;
            main(()->assertTrue(AgendaWidgetProvider.completeTask(store,widgetId)));
            assertTrue(record(widgetId).data.completed);long completedRevision=record(widgetId).revision;int pending=store.pendingCount();
            assertTrue("First widget completion did not persist",completedRevision>initialRevision);
            main(()->assertTrue(AgendaWidgetProvider.completeTask(store,widgetId)));
            assertTrue("Repeated widget completion toggled back to pending",record(widgetId).data.completed);
            assertEquals("Repeated completion rewrote the record",completedRevision,record(widgetId).revision);
            assertEquals("Repeated completion added a queued mutation",pending,store.pendingCount());
            assertFalse(record(widgetId).deleted);assertOffline();
            report.append("PASS production widget completion helper is idempotent: second completion keeps completed/revision/pending unchanged\n");
            assertRemoteViewsApplyAndReapply(circleId);
            report.append("NOT VERIFIED here: system widget binding, receiver host/lock-screen validation, and physical fold hinge.\n");
        } catch(Throwable failure) {
            report.append("FAIL ").append(failure.getClass().getSimpleName()).append(": ").append(failure.getMessage()).append('\n');
            try{screenshot("failure-screen.png");}catch(Exception ignored){}
            throw failure;
        } finally {
            try(FileOutputStream out=new FileOutputStream(new File(artifacts,"appearance-report.txt"))){out.write(report.toString().getBytes(StandardCharsets.UTF_8));}
            main(()->{Activity a=resumed.get();if(a!=null&&!a.isFinishing())a.finish();application.unregisterActivityLifecycleCallbacks(lifecycle);});
            shell("cmd uimode night no");shell("wm size reset");shell("wm density reset");
        }
    }

    private Activity current(){return resumed.get();}
    private void assertRemoteViewsApplyAndReapply(String pendingId)throws Exception {
        // Invoke the existing private renderer, avoiding any production-only-for-tests entry point.
        // No PendingIntent is sent and no fabricated widget id is granted host privileges.
        Class<?> metricsClass=Class.forName("app.youmu.daynote.AgendaWidgetProvider$Metrics");
        Constructor<?> constructor=metricsClass.getDeclaredConstructor(Context.class);constructor.setAccessible(true);
        Method build=AgendaWidgetProvider.class.getDeclaredMethod("build",Context.class,int.class,int.class,int.class,
                boolean.class,List.class,CalendarStore.class,boolean.class,metricsClass);build.setAccessible(true);
        List<SyncCore.Record> records=Collections.singletonList(record(pendingId));
        for(int layout:new int[]{R.layout.widget_agenda_compact,R.layout.widget_agenda_wide,R.layout.widget_agenda_tall}) {
            main(()->{DisplayPreferences.setTheme(context,"light");DisplayPreferences.setFontScale(context,1.5f);});
            RemoteViews light=(RemoteViews)build.invoke(null,context,170000,layout,5,true,records,store,false,constructor.newInstance(context));
            AtomicReference<View> rendered=new AtomicReference<>();AtomicReference<Float> largeSize=new AtomicReference<>();AtomicReference<Integer> lightColor=new AtomicReference<>();
            main(()->{View view=light.apply(context,new FrameLayout(context));rendered.set(view);
                TextView label=view.findViewById(R.id.widget_label_1);assertTrue(label.getText().toString().contains(CIRCLE_TITLE));
                largeSize.set(label.getTextSize());lightColor.set(label.getCurrentTextColor());
                assertNotNull(view.findViewById(R.id.widget_complete_1));assertTrue(view.findViewById(R.id.widget_complete_1).isClickable());});
            main(()->{DisplayPreferences.setTheme(context,"dark");DisplayPreferences.setFontScale(context,.85f);});
            RemoteViews dark=(RemoteViews)build.invoke(null,context,170000,layout,5,true,records,store,false,constructor.newInstance(context));
            main(()->{dark.reapply(context,rendered.get());TextView label=rendered.get().findViewById(R.id.widget_label_1);
                assertNotEquals("Widget theme did not update on reapply",lightColor.get().intValue(),label.getCurrentTextColor());
                assertTrue("Widget font did not update on reapply",label.getTextSize()<largeSize.get());});
        }
        report.append("PASS compact/wide/tall production RemoteViews apply/reapply, with font and theme changes; no launcher binding simulated\n");
    }
    private void assertOffline(){assertFalse(store.connected());assertNull(store.snapshot().accountId);}
    private String seed(String title,int hour) {
        SyncCore.TaskData data=new SyncCore.TaskData();data.title=title;data.notes="离线测试虚构安排。";data.reminderMinutes=-1;
        data.dueAt=LocalDate.now().atTime(hour,0).atZone(ZoneId.systemDefault()).toInstant().toString();
        main(()->assertTrue(store.save(null,data,-1)));
        return store.list().stream().filter(r->title.equals(r.data.title)).findFirst().orElseThrow().id;
    }
    private SyncCore.Record record(String id){SyncCore.Record r=store.snapshot().records.get(id);assertNotNull(r);return r;}
    private void resize(int width,int height)throws Exception {
        assertEquals("1",shell("getprop ro.kernel.qemu").trim());
        assertTrue((width==1080&&height==2400)||(width==2280&&height==1980));
        shell("wm size "+width+"x"+height);shell("wm density 480");SystemClock.sleep(800);idle();
    }
    private void openSettings()throws Exception {clickActivity(v->v instanceof Button t&&"设置".contentEquals(t.getText()));awaitText("跟随系统");}
    private void closeSettings()throws Exception {clickText("完成");idle();}
    private void chooseTheme(String label,String value)throws Exception {
        Activity before=current();clickText(label);awaitRecreated(before);awaitText("跟随系统");assertEquals(value,DisplayPreferences.theme(context));
    }
    private void chooseFont(String label,float scale)throws Exception {
        clickNode(n->nodeText(n).startsWith("字体大小 ·"));awaitText("字体大小");
        Activity before=current();clickText(label);awaitRecreated(before);awaitText("跟随系统");assertEquals(scale,DisplayPreferences.fontScale(context),.001f);
    }
    private void awaitRecreated(Activity previous)throws Exception {
        waitFor(()->current()!=null&&current()!=previous&&!current().isDestroyed(),"Activity was not recreated");idle();
    }
    private float headerTextSize(){AtomicReference<Float> size=new AtomicReference<>();main(()->size.set(header().getTextSize()));return size.get();}
    private TextView header(){View view=findView(current().findViewById(android.R.id.content),v->v instanceof TextView t&&"朝夕日历".contentEquals(t.getText()));assertNotNull(view);return (TextView)view;}
    private void assertHeaderContrast(boolean dark) {
        assertTrue("Actual header color does not match selected theme",headerMatchesTheme(dark));
    }
    private boolean headerMatchesTheme(boolean dark){AtomicReference<Boolean> matches=new AtomicReference<>(false);main(()->{
        Activity activity=current();if(activity==null||activity.isDestroyed())return;
        View view=findView(activity.findViewById(android.R.id.content),v->v instanceof TextView t&&"朝夕日历".contentEquals(t.getText()));
        if(view instanceof TextView label){int color=label.getCurrentTextColor();int brightness=(Color.red(color)+Color.green(color)+Color.blue(color))/3;matches.set(dark?brightness>180:brightness<100);}
    });return matches.get();}
    private boolean hasActivityText(String text){AtomicReference<Boolean> found=new AtomicReference<>();main(()->found.set(findView(current().findViewById(android.R.id.content),v->v instanceof TextView t&&text.contentEquals(t.getText()))!=null));return found.get();}
    private void clickFilter(String label)throws Exception {clickActivity(v->v instanceof Button t&&label.contentEquals(t.getText()));idle();}
    private void clickActivity(Predicate<View> predicate)throws Exception {
        main(()->{View view=findView(current().findViewById(android.R.id.content),predicate);assertNotNull("Missing native activity control",view);Rect area=new Rect();view.getDrawingRect(area);view.requestRectangleOnScreen(area,true);});idle();
        main(()->{View view=findView(current().findViewById(android.R.id.content),predicate);assertNotNull(view);assertTrue("Control is not enabled",view.isEnabled());assertTrue("Control is off-screen",view.getGlobalVisibleRect(new Rect()));assertTrue("Native control click failed",view.performClick());});idle();
    }
    private View findView(View root,Predicate<View> predicate){if(predicate.test(root))return root;if(root instanceof ViewGroup group)for(int i=0;i<group.getChildCount();i++){View result=findView(group.getChildAt(i),predicate);if(result!=null)return result;}return null;}
    private void setNotes(String notes)throws Exception {
        AccessibilityNodeInfo node=await(n->n.isEditable()&&n.getHintText()!=null&&n.getHintText().toString().startsWith("补充地点"));
        Bundle args=new Bundle();args.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE,notes);
        assertTrue(node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT,args));idle();awaitText(notes);
    }
    private void clickText(String text)throws Exception {clickNode(n->text.equals(nodeText(n)));}
    private void clickNode(Predicate<AccessibilityNodeInfo> predicate)throws Exception {
        AccessibilityNodeInfo node=await(predicate);while(node!=null&&!node.isClickable())node=node.getParent();assertNotNull("No clickable native control",node);
        assertTrue(node.performAction(AccessibilityNodeInfo.ACTION_CLICK));idle();
    }
    private void awaitText(String text)throws Exception {await(n->text.equals(nodeText(n)));}
    private AccessibilityNodeInfo await(Predicate<AccessibilityNodeInfo> predicate)throws Exception {
        long end=SystemClock.elapsedRealtime()+12000;
        do{AccessibilityNodeInfo found=findNode(automation.getRootInActiveWindow(),predicate);if(found!=null)return found;SystemClock.sleep(120);}while(SystemClock.elapsedRealtime()<end);
        throw new AssertionError("Expected native dialog control was not found");
    }
    private AccessibilityNodeInfo findNode(AccessibilityNodeInfo root,Predicate<AccessibilityNodeInfo> predicate){if(root==null)return null;if(predicate.test(root))return root;for(int i=0;i<root.getChildCount();i++){AccessibilityNodeInfo n=findNode(root.getChild(i),predicate);if(n!=null)return n;}return null;}
    private String nodeText(AccessibilityNodeInfo node){return node.getText()==null?"":node.getText().toString();}
    private void waitFor(BooleanSupplier condition,String error)throws Exception {long end=SystemClock.elapsedRealtime()+12000;do{if(condition.getAsBoolean())return;SystemClock.sleep(120);}while(SystemClock.elapsedRealtime()<end);throw new AssertionError(error);}
    private void idle(){instrumentation.waitForIdleSync();try{automation.waitForIdle(250,3000);}catch(Exception ignored){}SystemClock.sleep(150);}
    private void main(Runnable task){instrumentation.runOnMainSync(task);}
    private String shell(String command)throws Exception {
        try(ParcelFileDescriptor fd=automation.executeShellCommand(command);ParcelFileDescriptor.AutoCloseInputStream input=new ParcelFileDescriptor.AutoCloseInputStream(fd);ByteArrayOutputStream output=new ByteArrayOutputStream()){
            byte[] bytes=new byte[4096];int count;while((count=input.read(bytes))!=-1)output.write(bytes,0,count);return output.toString(StandardCharsets.UTF_8.name());
        }
    }
    private void screenshot(String name)throws Exception {
        idle();Bitmap bitmap=automation.takeScreenshot();assertNotNull(bitmap);
        try(FileOutputStream output=new FileOutputStream(new File(artifacts,name))){assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG,100,output));}finally{bitmap.recycle();}
    }
}
