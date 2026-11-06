package app.youmu.daynote;

import android.app.Activity;
import android.app.Instrumentation;
import android.app.UiAutomation;
import android.appwidget.AppWidgetHost;
import android.appwidget.AppWidgetHostView;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProviderInfo;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.Rect;
import android.os.Bundle;
import android.os.ParcelFileDescriptor;
import android.os.SystemClock;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.accessibility.AccessibilityNodeInfo;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;

import org.junit.Test;
import org.junit.runner.RunWith;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.Predicate;

import static org.junit.Assert.*;

/** Isolated emulator-only UI checks. No cloud configuration, pairing key, or production test entry point. */
@RunWith(AndroidJUnit4.class)
public final class UiSmokeTest {
    private static final String QA_PACKAGE="app.youmu.daynote.android.qa";
    private static final String DRAFT_TITLE="折叠草稿检查（虚构）";
    private static final String DRAFT_NOTES="此内容仅用于模拟器界面检查，折叠和旋转后仍应保留。";
    private final Instrumentation instrumentation=InstrumentationRegistry.getInstrumentation();
    private final UiAutomation automation=instrumentation.getUiAutomation();
    private Activity activity;
    private Context context;
    private CalendarStore store;
    private File artifacts;
    private final StringBuilder report=new StringBuilder();

    @Test public void nativeCalendarResizeDraftAndWidget() throws Exception {
        context=instrumentation.getTargetContext();
        assertTrue("UI checks require a debug build",BuildConfig.DEBUG);
        assertEquals("UI checks may only use the independent QA package",QA_PACKAGE,context.getPackageName());
        assertEquals("Refusing display changes on a physical device","1",shell("getprop ro.kernel.qemu").trim());
        artifacts=new File(context.getExternalFilesDir(null),"ui-qa");
        assertTrue("Cannot create emulator evidence directory",artifacts.isDirectory()||artifacts.mkdirs());
        try {
            AtomicReference<CalendarStore> reference=new AtomicReference<>();
            main(()->reference.set(CalendarStore.get(context)));store=reference.get();
            assertFalse("The UI fixture must stay offline",store.connected());
            assertNull("The UI fixture must never reuse a cloud account",store.snapshot().accountId);
            assertTrue("Use a fresh emulator QA app for UI fixtures",store.list().isEmpty());
            seed("整理本周计划（虚构）",9,0,"blue",false,false);
            seed("散步与休息（虚构）",18,30,"teal",false,false);
            seed("提交阅读笔记（虚构）",16,0,"orange",true,false);
            seed("已完成的练习（虚构）",8,0,"green",false,true);
            resizeDisplay(1080,2400);
            activity=instrumentation.startActivitySync(new Intent(context,MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
            idle();awaitText("朝夕日历");screenshot("01-compact-360dp.png");
            assertLayout(false);assertCalendarColumnsVisible();report.append("PASS compact native calendar, all seven columns visible at 360 dp\n");

            resizeDisplay(1800,2400);idle();awaitText("朝夕日历");assertLayout(true);assertCalendarColumnsVisible();
            screenshot("02a-expanded-threshold-600dp.png");report.append("PASS two-pane threshold, all seven calendar columns visible at 600 dp\n");

            resizeDisplay(2280,1980);idle();awaitText("朝夕日历");assertLayout(true);assertCalendarColumnsVisible();
            screenshot("02-expanded-760dp.png");report.append("PASS expanded two-pane native calendar, all seven columns visible at 760 dp\n");
            clickText("设置");awaitText("显示与字体");screenshot("03-settings.png");clickText("完成");

            resizeDisplay(1080,2400);idle();
            main(()->activity.startActivity(new Intent(context,MainActivity.class).setAction(MainActivity.ACTION_NEW)
                    .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP)));
            awaitText("记下一件事");
            setEditable("这件事叫什么",DRAFT_TITLE);setEditable("补充地点",DRAFT_NOTES);
            resizeDisplay(2280,1980);idle();awaitText(DRAFT_TITLE);awaitText(DRAFT_NOTES);
            screenshot("04-editor-after-expand.png");
            assertTrue("Emulator rotation request failed",automation.setRotation(UiAutomation.ROTATION_FREEZE_90));
            SystemClock.sleep(900);idle();awaitText(DRAFT_TITLE);awaitText(DRAFT_NOTES);
            screenshot("05-editor-after-rotation.png");
            clickText("保存日程");idle();
            assertTrue("The saved draft did not reach the local store",store.list().stream().anyMatch(r->DRAFT_TITLE.equals(r.data.title)&&DRAFT_NOTES.equals(r.data.notes)));
            report.append("PASS editor draft retained across resize and rotation, saved offline\n");
            assertFalse("UI smoke must never connect to the cloud",store.connected());
            assertNull("UI smoke must never bind a cloud account",store.snapshot().accountId);

            automation.setRotation(UiAutomation.ROTATION_FREEZE_0);resizeDisplay(2280,1980);idle();
            tryWidgetCapture();
            report.append("UI smoke finished. This is emulator resizing, not a Samsung hinge or physical-device validation.\n");
        } catch(Throwable failure) {
            report.append("FAIL ").append(failure.getClass().getSimpleName()).append(": ").append(failure.getMessage()).append('\n');
            try{screenshot("failure-screen.png");}catch(Exception ignored){}
            throw failure;
        } finally {
            if(artifacts!=null)try(FileOutputStream output=new FileOutputStream(new File(artifacts,"ui-report.txt"))){output.write(report.toString().getBytes(StandardCharsets.UTF_8));}
            if(activity!=null)main(()->activity.finish());
            automation.setRotation(UiAutomation.ROTATION_UNFREEZE);
            shell("wm size reset");shell("wm density reset");
        }
    }
    private void seed(String title,int hour,int minute,String color,boolean deadline,boolean completed) {
        SyncCore.TaskData data=new SyncCore.TaskData();data.title=title;data.notes="模拟器演示数据，不代表真实安排。";
        data.dueAt=LocalDate.now().atTime(hour,minute).atZone(ZoneId.systemDefault()).toInstant().toString();
        data.colorId=color;data.isDeadline=deadline;data.completed=completed;data.reminderMinutes=-1;
        main(()->assertTrue("Cannot persist synthetic UI fixture",store.save(null,data,-1)));
    }
    private void resizeDisplay(int width,int height) throws Exception {
        assertEquals("Display changes are limited to the emulator","1",shell("getprop ro.kernel.qemu").trim());
        assertTrue("Unexpected emulator size",(width==1080&&height==2400)||(width==1800&&height==2400)||(width==2280&&height==1980));
        shell("wm size "+width+"x"+height);shell("wm density 480");SystemClock.sleep(900);idle();
    }
    private void assertLayout(boolean expanded) throws Exception {
        AccessibilityNodeInfo monthPrevious=await(n->"上个月".contentEquals(n.getContentDescription()==null?"":n.getContentDescription()));
        AccessibilityNodeInfo add=await(n->text(n).contains("新日程")&&n.isClickable());
        Rect left=new Rect(),right=new Rect();monthPrevious.getBoundsInScreen(left);add.getBoundsInScreen(right);
        if(expanded) {
            assertTrue("Expanded agenda should sit beside the calendar",Math.abs(left.centerY()-right.centerY())<180);
            assertTrue("Expanded agenda should use the right pane",right.left>left.right);
        } else assertTrue("Compact agenda should sit below the calendar",right.top>left.bottom+200);
    }
    private void assertCalendarColumnsVisible() {
        main(()->{
            ViewGroup headings=findWeekdayRow(activity.findViewById(android.R.id.content));
            assertNotNull("The calendar must have seven weekday headings",headings);
            ViewGroup grid=(ViewGroup)headings.getParent();
            ViewGroup firstWeek=(ViewGroup)grid.getChildAt(1);
            assertEquals("The first week must contain all seven date cells",7,firstWeek.getChildCount());
            float density=activity.getResources().getDisplayMetrics().density;
            for(int column=0;column<7;column++) {
                assertHorizontallyVisible(headings.getChildAt(column),"Weekday column "+column);
                View date=firstWeek.getChildAt(column);
                assertHorizontallyVisible(date,"Date column "+column);
                assertTrue("Date targets must be at least 44 dp wide",date.getWidth()+1>=44*density);
            }
        });
    }
    private ViewGroup findWeekdayRow(View view) {
        if(!(view instanceof ViewGroup group))return null;
        String[] weekdays={"一","二","三","四","五","六","日"};
        if(group.getChildCount()==7) {
            boolean matches=true;
            for(int i=0;i<7;i++)if(!(group.getChildAt(i) instanceof TextView label)||!weekdays[i].contentEquals(label.getText())){matches=false;break;}
            if(matches)return group;
        }
        for(int i=0;i<group.getChildCount();i++){ViewGroup found=findWeekdayRow(group.getChildAt(i));if(found!=null)return found;}
        return null;
    }
    private void assertHorizontallyVisible(View view,String label) {
        int[] position=new int[2];view.getLocationOnScreen(position);Rect visible=new Rect();
        assertTrue(label+" is off-screen",view.getGlobalVisibleRect(visible));
        assertTrue(label+" is clipped on the left",visible.left<=position[0]+1);
        assertTrue(label+" is clipped on the right",visible.right>=position[0]+view.getWidth()-1);
    }
    private void setEditable(String hint,String value) throws Exception {
        AccessibilityNodeInfo node=await(n->n.isEditable()&&n.getHintText()!=null&&n.getHintText().toString().contains(hint));
        Bundle arguments=new Bundle();arguments.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE,value);
        assertTrue("Unable to enter the synthetic editor draft",node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT,arguments));
        idle();awaitText(value);
    }
    private void clickText(String value) throws Exception {
        AccessibilityNodeInfo node=await(n->value.equals(text(n))&&n.isVisibleToUser());
        AccessibilityNodeInfo click=node;
        while(click!=null&&!click.isClickable())click=click.getParent();
        assertNotNull("No clickable control for "+value,click);
        assertTrue("Cannot click "+value,click.performAction(AccessibilityNodeInfo.ACTION_CLICK));idle();
    }
    private AccessibilityNodeInfo awaitText(String value) throws Exception {return await(n->value.equals(text(n)));}
    private AccessibilityNodeInfo await(Predicate<AccessibilityNodeInfo> predicate) throws Exception {
        long end=SystemClock.elapsedRealtime()+10000;
        do {AccessibilityNodeInfo node=find(automation.getRootInActiveWindow(),predicate);if(node!=null)return node;SystemClock.sleep(120);}while(SystemClock.elapsedRealtime()<end);
        throw new AssertionError("Expected native UI element was not found");
    }
    private AccessibilityNodeInfo find(AccessibilityNodeInfo root,Predicate<AccessibilityNodeInfo> predicate) {
        if(root==null)return null;if(predicate.test(root))return root;
        for(int i=0;i<root.getChildCount();i++){AccessibilityNodeInfo result=find(root.getChild(i),predicate);if(result!=null)return result;}
        return null;
    }
    private String text(AccessibilityNodeInfo node){return node.getText()==null?"":node.getText().toString();}
    private void idle(){instrumentation.waitForIdleSync();try{automation.waitForIdle(250,3000);}catch(Exception ignored){}SystemClock.sleep(160);}
    private void main(Runnable task){instrumentation.runOnMainSync(task);}
    private String shell(String command)throws Exception {
        try(ParcelFileDescriptor descriptor=automation.executeShellCommand(command);
            ParcelFileDescriptor.AutoCloseInputStream input=new ParcelFileDescriptor.AutoCloseInputStream(descriptor);
            ByteArrayOutputStream output=new ByteArrayOutputStream()){
            byte[] bytes=new byte[4096];int count;while((count=input.read(bytes))!=-1)output.write(bytes,0,count);
            return output.toString(StandardCharsets.UTF_8.name());
        }
    }
    private void screenshot(String name)throws Exception {
        idle();Bitmap bitmap=automation.takeScreenshot();assertNotNull("Android did not return a native screenshot",bitmap);
        try(FileOutputStream output=new FileOutputStream(new File(artifacts,name))){assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG,100,output));}
        finally{bitmap.recycle();}
    }
    private void tryWidgetCapture() throws Exception {
        AppWidgetManager manager=AppWidgetManager.getInstance(context);
        AtomicReference<AppWidgetHost> reference=new AtomicReference<>();
        main(()->reference.set(new AppWidgetHost(context,260066)));AppWidgetHost host=reference.get();
        int id=host.allocateAppWidgetId();
        try {
            ComponentName provider=new ComponentName(context,AgendaWidgetProvider.class);
            Bundle options=new Bundle();options.putInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH,320);options.putInt(AppWidgetManager.OPTION_APPWIDGET_MAX_WIDTH,320);
            options.putInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT,280);options.putInt(AppWidgetManager.OPTION_APPWIDGET_MAX_HEIGHT,280);
            boolean bound=manager.bindAppWidgetIdIfAllowed(id,provider,options);
            if(!bound){
                main(()->activity.startActivity(new Intent(AppWidgetManager.ACTION_APPWIDGET_BIND)
                        .putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID,id).putExtra(AppWidgetManager.EXTRA_APPWIDGET_PROVIDER,provider)
                        .putExtra(AppWidgetManager.EXTRA_APPWIDGET_OPTIONS,options)));
                idle();
                AccessibilityNodeInfo consent=find(automation.getRootInActiveWindow(),n->n.isClickable()&&("Allow".equalsIgnoreCase(text(n))||"允许".equals(text(n))));
                if(consent!=null){consent.performAction(AccessibilityNodeInfo.ACTION_CLICK);idle();}
                bound=manager.getAppWidgetInfo(id)!=null;
            }
            if(!bound){report.append("NOT VERIFIED widget host: system binding consent was unavailable; no substitute widget image created.\n");return;}
            AppWidgetProviderInfo info=manager.getAppWidgetInfo(id);assertNotNull(info);
            main(()->{
                host.startListening();AppWidgetHostView widget=host.createView(activity,id,info);
                LinearLayout page=new LinearLayout(activity);page.setOrientation(LinearLayout.VERTICAL);page.setGravity(Gravity.CENTER);page.setBackgroundColor(Color.rgb(243,239,232));
                TextView label=new TextView(activity);label.setText("朝夕 · 原生日程小组件");label.setTextSize(20);label.setTextColor(Color.rgb(45,43,39));label.setGravity(Gravity.CENTER);
                page.addView(label,new LinearLayout.LayoutParams(-1,100));
                float density=activity.getResources().getDisplayMetrics().density;
                page.addView(widget,new LinearLayout.LayoutParams(Math.round(320*density),Math.round(280*density)));
                activity.setContentView(page);widget.updateAppWidgetSize(options,320,280,320,280);AgendaWidgetProvider.refreshAll(activity);
            });
            idle();await(n->text(n).contains("整理本周计划（虚构）"));screenshot("06-real-appwidget-host.png");
            report.append("PASS real AppWidgetHost / RemoteViews render using system-approved binding\n");
        } catch(Exception | AssertionError widgetFailure) {
            report.append("NOT VERIFIED widget host: ").append(widgetFailure.getClass().getSimpleName()).append('\n');
            try{screenshot("widget-host-diagnostic.png");}catch(Exception ignored){}
        } finally {main(host::stopListening);host.deleteAppWidgetId(id);}
    }
}
