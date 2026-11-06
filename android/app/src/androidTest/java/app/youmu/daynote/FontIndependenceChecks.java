package app.youmu.daynote;

import android.app.Activity;
import android.app.Application;
import android.app.Instrumentation;
import android.app.UiAutomation;
import android.content.Context;
import android.graphics.Bitmap;
import android.os.Bundle;
import android.os.SystemClock;
import android.view.View;
import android.view.ViewGroup;
import android.view.accessibility.AccessibilityNodeInfo;
import android.widget.FrameLayout;
import android.widget.RemoteViews;
import android.widget.TextView;

import java.io.File;
import java.io.FileOutputStream;
import java.lang.reflect.Constructor;
import java.lang.reflect.Method;
import java.time.LocalDate;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.BooleanSupplier;
import java.util.function.Predicate;

import static org.junit.Assert.*;

/** Shared real-UI checks, safe for the emulator and explicitly selected empty QA device profiles. */
final class FontIndependenceChecks {
    private final Instrumentation instrumentation;
    private final UiAutomation automation;
    private final Context context;
    private final CalendarStore store;
    private final AtomicReference<Activity> activity;
    private final Application application;
    private boolean settingsOpen;
    private final Application.ActivityLifecycleCallbacks lifecycle=new Application.ActivityLifecycleCallbacks() {
        @Override public void onActivityResumed(Activity a){if(a instanceof MainActivity)activity.set(a);}
        @Override public void onActivityCreated(Activity a,Bundle b){}
        @Override public void onActivityStarted(Activity a){}
        @Override public void onActivityPaused(Activity a){}
        @Override public void onActivityStopped(Activity a){}
        @Override public void onActivitySaveInstanceState(Activity a,Bundle b){}
        @Override public void onActivityDestroyed(Activity a){}
    };
    private FontIndependenceChecks(Instrumentation instrumentation,Context context,CalendarStore store,Activity initial) {
        this.instrumentation=instrumentation;this.automation=instrumentation.getUiAutomation();this.context=context;this.store=store;
        this.activity=new AtomicReference<>(initial);this.application=(Application)context.getApplicationContext();
    }
    static String run(Instrumentation instrumentation,Context context,CalendarStore store,Activity initial,File artifacts)throws Exception {
        assertTrue(BuildConfig.DEBUG);assertEquals("app.youmu.daynote.android.qa",context.getPackageName());
        assertFalse(store.connected());assertNull(store.snapshot().accountId);
        FontIndependenceChecks check=new FontIndependenceChecks(instrumentation,context,store,initial);
        check.main(()->check.application.registerActivityLifecycleCallbacks(check.lifecycle));
        try{return check.verify(artifacts);}finally{check.main(()->check.application.unregisterActivityLifecycleCallbacks(check.lifecycle));}
    }
    private String verify(File artifacts)throws Exception {
        selectSize(false,100);selectSize(true,100);closeSettings();
        float app100=headerPixels(),widget100=widgetPixels();
        selectSize(false,150);closeSettings();
        float app150=headerPixels();
        assertTrue("App size must visibly increase",app150>app100+1);
        assertEquals("App size changed actual RemoteViews text",widget100,widgetPixels(),.75f);
        assertEquals(1f,DisplayPreferences.widgetFontScale(context),.001f);
        selectSize(true,200);closeSettings();
        float widget200=widgetPixels();
        assertEquals("Widget size changed actual App header",app150,headerPixels(),.75f);
        assertTrue("Widget 200% must enlarge actual RemoteViews text",widget200>widget100+1);
        assertEquals(1.5f,DisplayPreferences.appFontScale(context),.001f);
        Activity before=activity.get();main(before::recreate);awaitReplacement(before);idle();
        assertEquals(1.5f,DisplayPreferences.appFontScale(context),.001f);assertEquals(2f,DisplayPreferences.widgetFontScale(context),.001f);
        assertEquals(app150,headerPixels(),.75f);assertEquals(widget200,widgetPixels(),.75f);
        selectSize(false,85);closeSettings();
        float app85=headerPixels();assertTrue(app85<app150);
        assertEquals("App 85% changed persisted Widget 200% rendering",widget200,widgetPixels(),.75f);
        selectSize(true,150);closeSettings();
        float widget150=widgetPixels();assertTrue(widget150>widget100&&widget150<widget200);
        assertEquals("Widget 150% changed App 85% rendering",app85,headerPixels(),.75f);
        before=activity.get();main(before::recreate);awaitReplacement(before);idle();
        assertEquals(.85f,DisplayPreferences.appFontScale(context),.001f);assertEquals(1.5f,DisplayPreferences.widgetFontScale(context),.001f);
        assertEquals(app85,headerPixels(),.75f);assertEquals(widget150,widgetPixels(),.75f);
        openSettings();screenshot(artifacts,"07-independent-font-settings.png");
        assertFalse(store.connected());assertNull(store.snapshot().accountId);
        return "PASS independent fonts through actual settings and activity recreation: App pixels 100/150/85="+app100+"/"+app150+"/"+app85
                +", Widget pixels 100/200/150="+widget100+"/"+widget200+"/"+widget150
                +"; widget edits keep the same Activity, App edits preserve widget rendering. Pure preview has matching pixels, no actions or data/preference writes. Final QA preferences App85/Widget150.\n";
    }
    private void selectSize(boolean widget,int percent)throws Exception {
        openSettings();float wanted=percent/100f;
        if(Math.abs((widget?DisplayPreferences.widgetFontScale(context):DisplayPreferences.appFontScale(context))-wanted)<.001f)return;
        String title=widget?"小组件字号":"应用内字号";
        click(n->text(n).startsWith(title+" ·"));await(n->title.equals(text(n)));
        Activity before=activity.get();click(n->text(n).endsWith("（"+percent+"%）"));
        if(widget) {
            waitFor(()->Math.abs(DisplayPreferences.widgetFontScale(context)-wanted)<.001f,"Widget size was not saved");
            idle();assertSame("Editing Widget size must not recreate the Activity",before,activity.get());
        } else awaitReplacement(before);
        await(n->"跟随系统".equals(text(n)));idle();settingsOpen=true;
    }
    private void openSettings()throws Exception {
        if(settingsOpen)return;
        click(n->"设置：字体、外观与同步".contentEquals(n.getContentDescription()==null?"":n.getContentDescription()));
        await(n->"跟随系统".equals(text(n)));settingsOpen=true;
    }
    private void closeSettings()throws Exception {if(settingsOpen){click(n->"完成".equals(text(n)));settingsOpen=false;idle();}}
    private void awaitReplacement(Activity previous)throws Exception {waitFor(()->activity.get()!=previous&&!activity.get().isDestroyed(),"App font change did not recreate Activity");idle();}
    private float headerPixels(){AtomicReference<Float> size=new AtomicReference<>();main(()->{TextView label=findText(activity.get().findViewById(android.R.id.content),"朝夕日历");assertNotNull(label);size.set(label.getTextSize());});return size.get();}
    private TextView findText(View view,String text){if(view instanceof TextView label&&text.contentEquals(label.getText()))return label;if(view instanceof ViewGroup group)for(int i=0;i<group.getChildCount();i++){TextView result=findText(group.getChildAt(i),text);if(result!=null)return result;}return null;}
    private float widgetPixels()throws Exception {
        // Pass the scaled Activity into the real renderer deliberately: widget code must normalize
        // to application/system resources. Apply it using an ordinary, unscaled host Context.
        Class<?> metricsClass=Class.forName("app.youmu.daynote.AgendaWidgetProvider$Metrics");
        Constructor<?> ctor=metricsClass.getDeclaredConstructor(Context.class);ctor.setAccessible(true);
        Method build=AgendaWidgetProvider.class.getDeclaredMethod("build",Context.class,int.class,int.class,int.class,
                boolean.class,List.class,CalendarStore.class,boolean.class,metricsClass);build.setAccessible(true);
        SyncCore.Record fictional=new SyncCore.Record();fictional.data.title="字号独立检查（虚构）";fictional.data.hasTime=false;
        fictional.data.dueAt=LocalDate.now().toString();fictional.data.reminderMinutes=-1;
        Context renderContext=activity.get();
        RemoteViews views=(RemoteViews)build.invoke(null,renderContext,170001,R.layout.widget_agenda_wide,1,true,
                Collections.singletonList(fictional),store,false,ctor.newInstance(renderContext));
        String stateBefore=store.snapshot().toJson().toString();
        java.util.Map<String,?> prefsBefore=new java.util.HashMap<>(context.getApplicationContext().getSharedPreferences("appearance",Context.MODE_PRIVATE).getAll());
        AtomicReference<Float> result=new AtomicReference<>();
        main(()->{Context host=context.getApplicationContext();View view=views.apply(host,new FrameLayout(host));TextView label=view.findViewById(R.id.widget_label_1);
            assertTrue(label.getText().toString().contains("字号独立检查"));result.set(label.getTextSize());
            View preview=AgendaWidgetProvider.preview(renderContext,new FrameLayout(host));TextView previewLabel=preview.findViewById(R.id.widget_label_1);
            assertTrue("Preview must show its fictional example",previewLabel.getText().toString().contains("阅读"));
            assertEquals("Preview inherited App font scaling",label.getTextSize(),previewLabel.getTextSize(),.75f);assertInert(preview);
        });
        assertEquals("Preview changed calendar state",stateBefore,store.snapshot().toJson().toString());
        assertEquals("Preview changed saved preferences",prefsBefore,context.getApplicationContext().getSharedPreferences("appearance",Context.MODE_PRIVATE).getAll());
        return result.get();
    }
    private void assertInert(View view){assertFalse("Preview must not contain clickable controls",view.isClickable());assertFalse(view.isLongClickable());assertFalse(view.isFocusable());if(view instanceof ViewGroup group)for(int i=0;i<group.getChildCount();i++)assertInert(group.getChildAt(i));}
    private void click(Predicate<AccessibilityNodeInfo> predicate)throws Exception {
        AccessibilityNodeInfo node=await(predicate);node.performAction(AccessibilityNodeInfo.ACTION_SHOW_ON_SCREEN);idle();node=await(predicate);
        while(node!=null&&!node.isClickable())node=node.getParent();assertNotNull("Control is not clickable",node);assertTrue(node.performAction(AccessibilityNodeInfo.ACTION_CLICK));idle();
    }
    private AccessibilityNodeInfo await(Predicate<AccessibilityNodeInfo> predicate)throws Exception {
        long deadline=SystemClock.elapsedRealtime()+12000;
        do{AccessibilityNodeInfo root=automation.getRootInActiveWindow(),found=find(root,predicate);if(found!=null)return found;
            SystemClock.sleep(150);
        }while(SystemClock.elapsedRealtime()<deadline);throw new AssertionError("Expected font settings control was not found");
    }
    private AccessibilityNodeInfo find(AccessibilityNodeInfo node,Predicate<AccessibilityNodeInfo> predicate){if(node==null)return null;if(predicate.test(node))return node;for(int i=0;i<node.getChildCount();i++){AccessibilityNodeInfo found=find(node.getChild(i),predicate);if(found!=null)return found;}return null;}
    private String text(AccessibilityNodeInfo node){return node.getText()==null?"":node.getText().toString();}
    private void waitFor(BooleanSupplier condition,String error)throws Exception {long end=SystemClock.elapsedRealtime()+12000;do{if(condition.getAsBoolean())return;SystemClock.sleep(120);}while(SystemClock.elapsedRealtime()<end);throw new AssertionError(error);}
    private void main(Runnable task){instrumentation.runOnMainSync(task);}
    private void idle(){instrumentation.waitForIdleSync();try{automation.waitForIdle(200,2500);}catch(Exception ignored){}SystemClock.sleep(120);}
    private void screenshot(File folder,String name)throws Exception {if(folder==null)return;assertTrue(folder.isDirectory()||folder.mkdirs());idle();Bitmap bitmap=automation.takeScreenshot();assertNotNull(bitmap);try(FileOutputStream out=new FileOutputStream(new File(folder,name))){assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG,100,out));}finally{bitmap.recycle();}}
}
