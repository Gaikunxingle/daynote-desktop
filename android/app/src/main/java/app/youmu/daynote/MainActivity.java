package app.youmu.daynote;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.Intent;
import android.content.res.Configuration;
import android.graphics.Color;
import android.graphics.Rect;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.text.InputType;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.RadioButton;
import android.widget.RadioGroup;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;
import androidx.core.util.Consumer;
import androidx.window.java.layout.WindowInfoTrackerCallbackAdapter;
import androidx.window.layout.DisplayFeature;
import androidx.window.layout.FoldingFeature;
import androidx.window.layout.WindowInfoTracker;
import androidx.window.layout.WindowLayoutInfo;
import java.time.Instant;
import java.time.LocalDate;
import java.time.YearMonth;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;

/** Native phone/foldable calendar. Data and credentials are owned by CalendarStore. */
public final class MainActivity extends Activity implements CalendarStore.Listener {
    public static final String ACTION_NEW = "app.youmu.daynote.NEW_TASK";
    public static final String ACTION_OPEN = "app.youmu.daynote.OPEN_TASK";
    public static final String EXTRA_TASK_ID = "task_id";
    public static final String EXTRA_DATE = "date";
    private static final String[] FILTERS = {"当天", "全部", "待安排", "截止", "已完成"};
    private DaynoteUi ui;
    private CalendarStore store;
    private LocalDate selectedDate = LocalDate.now();
    private YearMonth month = YearMonth.now();
    private int filter;
    private LinearLayout root, footer;
    private FrameLayout host;
    private ScrollView leftScroll, rightScroll;
    private TextView statusText;
    private Button syncButton;
    private int leftY, rightY, lastWidth = -1, lastHeight = -1;
    private TaskEditorDialog editor;
    private AlertDialog settingsDialog;
    private TextView settingsStatus;
    private Button connectButton, disconnectButton;
    private boolean destroying, renderQueued, lastConnected;
    private WindowInfoTrackerCallbackAdapter windowTracker;
    private Rect foldBounds;
    private boolean verticalFold;
    private final Consumer<WindowLayoutInfo> foldListener = info -> {
        Rect next = null; boolean vertical = false;
        for (DisplayFeature feature : info.getDisplayFeatures()) {
            if (feature instanceof FoldingFeature) {
                FoldingFeature fold = (FoldingFeature)feature;
                next = new Rect(fold.getBounds()); vertical = fold.getOrientation() == FoldingFeature.Orientation.VERTICAL;
                break;
            }
        }
        if (!java.util.Objects.equals(next,foldBounds) || vertical != verticalFold) {
            foldBounds = next; verticalFold = vertical; queueRender();
        }
    };

    @Override protected void attachBaseContext(Context base) {
        super.attachBaseContext(DisplayPreferences.wrap(base));
    }

    @Override public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        ui = new DaynoteUi(this); store = CalendarStore.get(this);
        if (savedInstanceState != null) {
            try { selectedDate = LocalDate.parse(savedInstanceState.getString("selected")); month = YearMonth.parse(savedInstanceState.getString("month")); } catch (RuntimeException ignored) {}
            filter = Math.max(0,Math.min(FILTERS.length-1,savedInstanceState.getInt("filter")));
            leftY = savedInstanceState.getInt("leftY"); rightY = savedInstanceState.getInt("rightY");
        }
        makeShell(); store.addListener(this); lastConnected = store.connected();
        AgendaWidgetProvider.refreshAll(this); ReminderScheduler.reschedule(this); SyncJobService.ensureScheduled(this);
        windowTracker = new WindowInfoTrackerCallbackAdapter(WindowInfoTracker.getOrCreate(this));
        if (savedInstanceState != null) {
            Bundle draft = savedInstanceState.getBundle("editorDraft");
            if (draft != null) root.post(() -> openEditor(null,null,draft));
            else if (savedInstanceState.getBoolean("settingsOpen")) root.post(this::showSettings);
        } else root.post(() -> handleIntent(getIntent()));
    }
    @Override protected void onStart() {
        super.onStart();
        windowTracker.addWindowLayoutInfoListener(this, command -> runOnUiThread(command), foldListener);
    }
    @Override protected void onResume() { super.onResume(); store.setActive(true); updateStatus(); }
    @Override protected void onPause() { store.setActive(false); super.onPause(); }
    @Override protected void onStop() { if (windowTracker != null) windowTracker.removeWindowLayoutInfoListener(foldListener); super.onStop(); }
    @Override protected void onDestroy() {
        destroying = true; store.removeListener(this);
        if (editor != null) editor.dismiss(); if (settingsDialog != null) settingsDialog.dismiss();
        super.onDestroy();
    }
    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent); setIntent(intent); handleIntent(intent); }
    @Override protected void onSaveInstanceState(Bundle out) {
        super.onSaveInstanceState(out); captureScroll();
        out.putString("selected",selectedDate.toString()); out.putString("month",month.toString()); out.putInt("filter",filter);
        out.putInt("leftY",leftY); out.putInt("rightY",rightY);
        if(editor!=null && editor.isShowing()) out.putBundle("editorDraft",editor.saveDraft());
        out.putBoolean("settingsOpen",settingsDialog!=null && settingsDialog.isShowing());
    }
    @Override public void onConfigurationChanged(Configuration configuration) {
        super.onConfigurationChanged(configuration);
        // Manifest normally allows recreation; this also handles host configurations that deliver an in-place resize.
        DaynoteUi next = new DaynoteUi(this);
        if(next.dark!=ui.dark) { recreate(); return; }
        if(editor!=null && editor.isShowing())editor.resizeForWindow();
        queueRender();
    }
    @Override public void onChanged() {
        if (destroying) return;
        AgendaWidgetProvider.refreshAll(this); ReminderScheduler.reschedule(this);
        if(lastConnected!=store.connected()) { lastConnected=store.connected(); SyncJobService.ensureScheduled(this); }
        updateStatus(); queueRender();
    }
    private void makeShell() {
        if(Build.VERSION.SDK_INT>=30) getWindow().setDecorFitsSystemWindows(false);
        getWindow().setStatusBarColor(ui.background); getWindow().setNavigationBarColor(ui.background);
        int flags=ui.dark?0:View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
        getWindow().getDecorView().setSystemUiVisibility(flags);
        root=ui.column(); root.setBackgroundColor(ui.background);
        root.setOnApplyWindowInsetsListener((v,insets)-> {
            int l,t,r,b;
            if(Build.VERSION.SDK_INT>=30) {
                android.graphics.Insets i=insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                l=i.left;t=i.top;r=i.right;b=i.bottom;
            } else {l=insets.getSystemWindowInsetLeft();t=insets.getSystemWindowInsetTop();r=insets.getSystemWindowInsetRight();b=insets.getSystemWindowInsetBottom();}
            v.setPadding(l,t,r,b); return insets;
        });
        LinearLayout header=ui.row(); header.setPadding(ui.dp(18),ui.dp(8),ui.dp(14),ui.dp(8));
        LinearLayout wordmark=ui.column();
        TextView overline=ui.text("DAYNOTE",10,ui.accent,true); overline.setLetterSpacing(.16f); wordmark.addView(overline);
        TextView name=ui.text("朝夕日历",26,ui.ink,true); name.setPadding(0,ui.dp(3),0,0); wordmark.addView(name);
        header.addView(wordmark,new LinearLayout.LayoutParams(0,-2,1));
        Button today=ui.button("今天",false,()-> { selectedDate=LocalDate.now();month=YearMonth.now();filter=0;rightY=0;if(rightScroll!=null)rightScroll.scrollTo(0,0);queueRender(); });
        header.addView(today,new LinearLayout.LayoutParams(-2,ui.dp(48)));
        Button settings=ui.button("设置",false,this::showSettings); settings.setContentDescription("设置：字体、外观与同步");
        LinearLayout.LayoutParams sp=new LinearLayout.LayoutParams(-2,ui.dp(48));sp.setMargins(ui.dp(6),0,0,0);header.addView(settings,sp);
        root.addView(header);
        host=new FrameLayout(this); root.addView(host,new LinearLayout.LayoutParams(-1,0,1));
        host.addOnLayoutChangeListener((v,l,t,r,b,ol,ot,or,ob)-> {
            if(r-l!=lastWidth || b-t!=lastHeight){lastWidth=r-l;lastHeight=b-t;queueRender();}
        });
        footer=ui.row();footer.setPadding(ui.dp(18),ui.dp(8),ui.dp(14),ui.dp(10));
        statusText=ui.text("",12,ui.muted,false);statusText.setMaxLines(2);statusText.setEllipsize(TextUtils.TruncateAt.END);
        footer.addView(statusText,new LinearLayout.LayoutParams(0,-2,1));
        syncButton=ui.button("连接",false,()-> { if(store.connected())store.syncAsync();else showSettings(); });footer.addView(syncButton);
        root.addView(footer);setContentView(root); root.requestApplyInsets();updateStatus();
    }
    private void queueRender() {
        if(root==null || destroying || renderQueued)return;
        renderQueued=true;root.post(()->{renderQueued=false;if(!destroying)render();});
    }
    private void captureScroll() { if(leftScroll!=null)leftY=leftScroll.getScrollY();if(rightScroll!=null)rightY=rightScroll.getScrollY(); }
    private void render() {
        if(host.getWidth()==0)return;
        captureScroll();host.removeAllViews();leftScroll=null;rightScroll=null;
        int width=host.getWidth(),height=host.getHeight();int[] location=new int[2];host.getLocationInWindow(location);
        Rect fold=foldBounds==null?null:new Rect(foldBounds);
        if(fold!=null)fold.offset(-location[0],-location[1]);
        boolean horizontal=fold!=null && !verticalFold && fold.top>ui.dp(100) && fold.bottom<height-ui.dp(100);
        boolean dual=width>=ui.dp(600) && !horizontal;
        List<SyncCore.Record> records=store.list();
        if(horizontal) {
            LinearLayout stack=ui.column();stack.setPadding(ui.dp(12),0,ui.dp(12),0);
            leftScroll=scroll(calendarPanel(records));rightScroll=scroll(agendaPanel(records));
            stack.addView(leftScroll,new LinearLayout.LayoutParams(-1,Math.max(ui.dp(100),fold.top-ui.dp(8))));
            stack.addView(ui.space(Math.max(16,Math.round((fold.height())/getResources().getDisplayMetrics().density)+16)));
            stack.addView(rightScroll,new LinearLayout.LayoutParams(-1,0,1));host.addView(stack,new FrameLayout.LayoutParams(-1,-1));
        } else if(dual) {
            LinearLayout panes=ui.row();panes.setGravity(Gravity.TOP);panes.setPadding(ui.dp(16),ui.dp(8),ui.dp(16),0);
            // Seven 48 dp dates plus the calendar's side padding must fit without horizontal scrolling.
            int contentWidth=width-ui.dp(32);int gap=ui.dp(24);int left=Math.max(ui.dp(348),(int)(contentWidth*.44));
            if(fold!=null && verticalFold && fold.centerX()>ui.dp(180) && fold.centerX()<width-ui.dp(180)) {
                left=fold.left-ui.dp(24);gap=Math.max(ui.dp(24),fold.width()+ui.dp(16));
            }
            left=Math.min(left,contentWidth-ui.dp(196)-gap);
            leftScroll=scroll(calendarPanel(records));rightScroll=scroll(agendaPanel(records));
            panes.addView(leftScroll,new LinearLayout.LayoutParams(left,-1));
            View gutter=new View(this);panes.addView(gutter,new LinearLayout.LayoutParams(gap,-1));
            panes.addView(rightScroll,new LinearLayout.LayoutParams(0,-1,1));host.addView(panes,new FrameLayout.LayoutParams(-1,-1));
        } else {
            LinearLayout page=ui.column();page.setPadding(ui.dp(12),ui.dp(8),ui.dp(12),ui.dp(20));
            page.addView(calendarPanel(records));page.addView(ui.space(24));page.addView(agendaPanel(records));
            rightScroll=scroll(page);host.addView(rightScroll,new FrameLayout.LayoutParams(-1,-1));
        }
        final ScrollView ls=leftScroll,rs=rightScroll;final int ly=leftY,ry=rightY;
        if(ls!=null)ls.post(()->ls.scrollTo(0,ly));if(rs!=null)rs.post(()->rs.scrollTo(0,ry));
    }
    private ScrollView scroll(View content) {
        ScrollView s=new ScrollView(this);s.setFillViewport(false);s.setClipToPadding(false);s.setVerticalScrollBarEnabled(false);s.addView(content);return s;
    }
    private View calendarPanel(List<SyncCore.Record> records) {
        LinearLayout panel=ui.column();panel.setPadding(ui.dp(6),ui.dp(16),ui.dp(6),ui.dp(16));panel.setBackground(ui.shape(ui.surface,22,true));
        LinearLayout bar=ui.row();bar.setPadding(ui.dp(10),0,ui.dp(4),ui.dp(8));
        LinearLayout titles=ui.column();titles.addView(ui.text(month.getMonthValue()+"月",25,ui.ink,true));
        TextView year=ui.text(month.getYear()+" · 日有安排，心有余地",11,ui.muted,false);year.setPadding(0,ui.dp(4),0,0);titles.addView(year);
        bar.addView(titles,new LinearLayout.LayoutParams(0,-2,1));
        Button back=ui.button("‹",false,()->{month=month.minusMonths(1);queueRender();});back.setTextSize(28);back.setContentDescription("上个月");back.setPadding(0,0,0,0);
        Button next=ui.button("›",false,()->{month=month.plusMonths(1);queueRender();});next.setTextSize(28);next.setContentDescription("下个月");next.setPadding(0,0,0,0);
        bar.addView(back,new LinearLayout.LayoutParams(ui.dp(48),ui.dp(48)));bar.addView(next,new LinearLayout.LayoutParams(ui.dp(48),ui.dp(48)));panel.addView(bar);
        // Narrow cover screens allow 44 dp cells; only exceptionally narrow windows need horizontal scrolling.
        HorizontalScrollView daysScroll=new HorizontalScrollView(this);daysScroll.setHorizontalScrollBarEnabled(false);daysScroll.setFillViewport(true);
        LinearLayout grid=ui.column();grid.setMinimumWidth(ui.dp(308));
        LinearLayout weekdays=ui.row();String[] days={"一","二","三","四","五","六","日"};
        for(int i=0;i<7;i++){TextView d=ui.text(days[i],11,i>=5?ui.accent:ui.muted,false);d.setGravity(Gravity.CENTER);weekdays.addView(d,new LinearLayout.LayoutParams(0,ui.dp(32),1));}
        grid.addView(weekdays);
        LocalDate first=month.atDay(1);int offset=first.getDayOfWeek().getValue()-1;int slots=((offset+month.lengthOfMonth()+6)/7)*7;
        LocalDate gridStart=first.minusDays(offset);
        int dotHeight=Math.max(ui.dp(13),ui.sp(11)+ui.dp(2));
        int cellHeight=Math.max(ui.dp(52),ui.sp(16)+dotHeight+ui.dp(10));
        for(int r=0;r<slots/7;r++){
            LinearLayout week=ui.row();
            for(int c=0;c<7;c++){
                LocalDate date=gridStart.plusDays(r*7L+c);boolean selected=date.equals(selectedDate);boolean inside=YearMonth.from(date).equals(month);
                LinearLayout cell=ui.column();cell.setGravity(Gravity.CENTER);cell.setMinimumWidth(ui.dp(44));cell.setMinimumHeight(ui.dp(52));
                cell.setBackground(ui.ripple(selected?ui.accent:Color.TRANSPARENT,13));
                TextView number=ui.text(Integer.toString(date.getDayOfMonth()),16,selected?(ui.dark?Color.BLACK:Color.WHITE):inside?ui.ink:ui.muted,date.equals(LocalDate.now()));
                number.setGravity(Gravity.CENTER);
                if(!inside)number.setAlpha(.45f);cell.addView(number,new LinearLayout.LayoutParams(-1,-2));
                long count=records.stream().filter(t->!t.data.completed&&date.equals(t.data.dueDate())).count();
                TextView dot=ui.text(count>0?"•":" ",11,selected?(ui.dark?Color.BLACK:Color.WHITE):ui.accent,true);dot.setGravity(Gravity.CENTER);cell.addView(dot,new LinearLayout.LayoutParams(-1,dotHeight));
                cell.setContentDescription(date.format(DateTimeFormatter.ofPattern("yyyy年M月d日 E",Locale.CHINA))+(count>0?"，"+count+"项日程":"")+(selected?"，已选择":""));
                cell.setFocusable(true);cell.setOnClickListener(v->{selectedDate=date;month=YearMonth.from(date);filter=0;rightY=0;if(rightScroll!=null)rightScroll.scrollTo(0,0);queueRender();});
                week.addView(cell,new LinearLayout.LayoutParams(0,cellHeight,1));
            }
            grid.addView(week);
        }
        daysScroll.addView(grid,new HorizontalScrollView.LayoutParams(-1,-2));panel.addView(daysScroll);
        long unfinished=records.stream().filter(r->!r.data.completed).count();
        TextView summary=ui.text(unfinished==0?"给今天留一点从容。":"还有 "+unfinished+" 件事，按自己的节奏来。",12,ui.muted,false);
        summary.setPadding(ui.dp(14),ui.dp(14),ui.dp(8),0);panel.addView(summary);return panel;
    }
    private View agendaPanel(List<SyncCore.Record> records) {
        LinearLayout panel=ui.column();panel.setPadding(ui.dp(2),0,ui.dp(2),ui.dp(12));
        LinearLayout top=ui.row();
        LinearLayout caption=ui.column();
        String subtitle=selectedDate.equals(LocalDate.now())?"TODAY":selectedDate.format(DateTimeFormatter.ofPattern("EEEE",Locale.ENGLISH)).toUpperCase(Locale.ROOT);
        TextView overline=ui.text(filter==0?subtitle:"YOUR CALENDAR",10,ui.accent,true);overline.setLetterSpacing(.12f);caption.addView(overline);
        TextView heading=ui.text(filter==0?selectedDate.format(DateTimeFormatter.ofPattern("M月d日 E",Locale.CHINA)):FILTERS[filter],21,ui.ink,true);heading.setPadding(0,ui.dp(5),0,0);caption.addView(heading);
        top.addView(caption,new LinearLayout.LayoutParams(0,-2,1));
        Button add=ui.button("+ 新日程",true,()->openEditor(null,selectedDate,null));add.setEnabled(store.canWrite());top.addView(add);
        panel.addView(top);panel.addView(ui.space(14));
        HorizontalScrollView filterScroll=new HorizontalScrollView(this);filterScroll.setHorizontalScrollBarEnabled(false);
        LinearLayout choices=ui.row();
        for(int i=0;i<FILTERS.length;i++){
            final int position=i;Button b=ui.button(FILTERS[i],false,()->{filter=position;rightY=0;if(rightScroll!=null)rightScroll.scrollTo(0,0);queueRender();});
            b.setTextColor(i==filter?ui.accent:ui.muted);b.setBackground(ui.ripple(i==filter?ui.softAccent:Color.TRANSPARENT,14));
            b.setSelected(i==filter);LinearLayout.LayoutParams lp=new LinearLayout.LayoutParams(-2,ui.dp(48));lp.setMargins(0,0,ui.dp(4),0);choices.addView(b,lp);
        }
        filterScroll.addView(choices);panel.addView(filterScroll);panel.addView(ui.space(12));
        List<SyncCore.Record> shown=new ArrayList<>();for(SyncCore.Record r:records)if(matches(r))shown.add(r);
        shown.sort(Comparator.comparing((SyncCore.Record r)->r.data.completed)
                .thenComparing(r->r.data.dueDate()==null?LocalDate.MAX:r.data.dueDate())
                .thenComparing(r->r.data.dueInstant()==null?Instant.MAX:r.data.dueInstant())
                .thenComparing(r->r.data.title));
        if(shown.isEmpty()){
            LinearLayout empty=ui.column();empty.setGravity(Gravity.CENTER);empty.setPadding(ui.dp(16),ui.dp(36),ui.dp(16),ui.dp(40));
            TextView symbol=ui.text("○",48,ui.accent,false);symbol.setGravity(Gravity.CENTER);empty.addView(symbol);
            TextView text=ui.text("这里还没有安排",17,ui.ink,true);text.setGravity(Gravity.CENTER);text.setPadding(0,ui.dp(12),0,ui.dp(8));empty.addView(text);
            TextView description=ui.text("记下一件小事，让今天有个开始。",13,ui.muted,false);description.setGravity(Gravity.CENTER);empty.addView(description);
            panel.addView(empty);
        } else for(SyncCore.Record r:shown){panel.addView(taskCard(r));panel.addView(ui.space(10));}
        return panel;
    }
    private boolean matches(SyncCore.Record r) {
        return switch(filter){case 1->!r.data.completed;case 2->r.data.dueAt==null&&!r.data.completed;case 3->r.data.isDeadline&&!r.data.completed;case 4->r.data.completed;default->!r.data.completed&&selectedDate.equals(r.data.dueDate());};
    }
    private View taskCard(SyncCore.Record r) {
        LinearLayout card=ui.row();card.setGravity(Gravity.TOP);card.setPadding(ui.dp(4),ui.dp(10),ui.dp(12),ui.dp(10));card.setBackground(ui.ripple(ui.surface,18));
        Button check=ui.completionButton(r.data.completed,ui.color(r.data.colorId),()->{store.setCompleted(r.id,!r.data.completed);updateStatus();});
        check.setContentDescription(r.data.completed?"恢复待办："+r.data.title:"完成："+r.data.title);check.setEnabled(store.canWrite());
        card.addView(check,new LinearLayout.LayoutParams(ui.dp(48),ui.dp(48)));
        LinearLayout details=ui.column();details.setPadding(ui.dp(2),ui.dp(5),0,ui.dp(4));
        TextView title=ui.text(r.data.title,16,r.data.completed?ui.muted:ui.ink,true);title.setMaxLines(3);title.setEllipsize(TextUtils.TruncateAt.END);
        if(r.data.completed)title.setPaintFlags(title.getPaintFlags()|android.graphics.Paint.STRIKE_THRU_TEXT_FLAG);details.addView(title);
        TextView time=ui.text(timeLabel(r.data),12,r.data.isDeadline?ui.accent:ui.muted,false);time.setPadding(0,ui.dp(7),0,0);details.addView(time);
        if(r.data.notes!=null&&!r.data.notes.isEmpty()) {TextView note=ui.text(r.data.notes,12,ui.muted,false);note.setMaxLines(2);note.setEllipsize(TextUtils.TruncateAt.END);note.setPadding(0,ui.dp(6),0,0);details.addView(note);}
        details.setMinimumHeight(ui.dp(48));details.setOnClickListener(v->openEditor(r,null,null));details.setFocusable(true);details.setContentDescription("编辑："+r.data.title);
        details.setOnLongClickListener(v->{showTaskActions(r);return true;});card.setOnLongClickListener(v->{showTaskActions(r);return true;});
        card.addView(details,new LinearLayout.LayoutParams(0,-2,1));
        Button more=ui.button("⋯",false,()->showTaskActions(r));more.setTextSize(24);more.setPadding(0,0,0,0);more.setBackground(ui.ripple(Color.TRANSPARENT,24));more.setContentDescription("更多操作："+r.data.title);
        card.addView(more,new LinearLayout.LayoutParams(ui.dp(48),ui.dp(48)));return card;
    }
    private String timeLabel(SyncCore.TaskData d) {
        String text;
        if(d.dueAt==null)text="未安排日期";
        else if(!d.hasTime)text=d.dueDate().format(DateTimeFormatter.ofPattern("M月d日"))+" · 全天";
        else {
            java.time.ZonedDateTime due=d.dueInstant().atZone(ZoneId.systemDefault());
            text=due.format(DateTimeFormatter.ofPattern(filter==0?"HH:mm":"M月d日 HH:mm"));
            if(d.endAt!=null)try{text+=" – "+java.time.OffsetDateTime.parse(d.endAt).atZoneSameInstant(ZoneId.systemDefault()).format(DateTimeFormatter.ofPattern("HH:mm"));}catch(RuntimeException ignored){}
        }
        if(d.isDeadline)text="截止 · "+text;
        if(d.reminderMinutes>=0)text+="  ·  已设提醒";return text;
    }
    private void showTaskActions(SyncCore.Record r) {
        new AlertDialog.Builder(this).setTitle(r.data.title).setItems(new String[]{"编辑",r.data.completed?"恢复待办":"标记完成","删除"},(d,which)-> {
            if(which==0)openEditor(r,null,null);else if(which==1)store.setCompleted(r.id,!r.data.completed);else confirmDelete(r);
        }).setNegativeButton("取消",null).show();
    }
    private void confirmDelete(SyncCore.Record r) {
        new AlertDialog.Builder(this).setTitle("删除这项日程？").setMessage(r.data.title+"\n删除也会同步到你的其他设备。")
                .setNegativeButton("保留",null).setPositiveButton("删除",(d,w)->store.delete(r.id)).show();
    }
    private void openEditor(SyncCore.Record record,LocalDate date,Bundle draft) {
        if(destroying)return;
        if(editor!=null&&editor.isShowing()){Toast.makeText(this,"请先保存或关闭当前编辑。",Toast.LENGTH_SHORT).show();return;}
        editor=new TaskEditorDialog(this,record,date,draft,(id,data,revision)->store.save(id,data,revision));
        editor.setOnDismissListener(d->{editor=null;queueRender();});editor.show();
    }
    private void handleIntent(Intent intent) {
        if(intent==null)return;String action=intent.getAction();
        if(ACTION_NEW.equals(action)){
            LocalDate date=selectedDate;try{String raw=intent.getStringExtra(EXTRA_DATE);if(raw!=null)date=LocalDate.parse(raw);}catch(RuntimeException ignored){}
            openEditor(null,date,null);
        } else if(ACTION_OPEN.equals(action)){
            String id=intent.getStringExtra(EXTRA_TASK_ID);SyncCore.Record match=null;for(SyncCore.Record r:store.list())if(r.id.equals(id)){match=r;break;}
            if(match==null)Toast.makeText(this,"这项日程已删除或尚未同步。",Toast.LENGTH_SHORT).show();
            else {if(match.data.dueDate()!=null){selectedDate=match.data.dueDate();month=YearMonth.from(selectedDate);filter=0;}else filter=2;queueRender();openEditor(match,null,null);}
        }
        if(ACTION_NEW.equals(action)||ACTION_OPEN.equals(action)){intent.setAction(Intent.ACTION_MAIN);intent.removeExtra(EXTRA_TASK_ID);intent.removeExtra(EXTRA_DATE);}
    }
    private void updateStatus() {
        if(statusText==null)return;
        String error=store.error();String text=error!=null&&!error.isEmpty()?error:store.status();
        statusText.setText(text==null||text.isEmpty()?"本机日历 · 离线也能记录":text);
        syncButton.setText(store.busy()?"同步中…":store.connected()?"同步":"连接");syncButton.setEnabled(!store.busy());
        if(settingsStatus!=null){settingsStatus.setText(accountSummary());}
        if(connectButton!=null){connectButton.setEnabled(!store.busy()&&store.canWrite());connectButton.setText(store.busy()?"连接中…":"连接我的日历");}
        if(disconnectButton!=null)disconnectButton.setEnabled(!store.busy()&&store.connected());
    }
    private String accountSummary() {
        String label=store.accountLabel();String text=store.connected()?"已连接："+label:"尚未连接设备";
        text+="\n待上传 "+store.pendingCount()+" 项";
        String last=store.lastSync();if(last!=null&&!last.isEmpty())try{text+=" · 上次 "+Instant.parse(last).atZone(ZoneId.systemDefault()).format(DateTimeFormatter.ofPattern("M月d日 HH:mm"));}catch(RuntimeException ignored){}
        String error=store.error();text+="\n"+(error!=null&&!error.isEmpty()?error:store.status());return text;
    }
    private void showSettings() {
        if(settingsDialog!=null&&settingsDialog.isShowing())return;
        LinearLayout page=ui.column();page.setPadding(ui.dp(22),ui.dp(12),ui.dp(22),ui.dp(20));
        page.addView(ui.text("显示与字体",15,ui.ink,true));page.addView(ui.space(8));
        RadioGroup modes=new RadioGroup(this);modes.setOrientation(RadioGroup.VERTICAL);
        String[] labels={"跟随系统","浅色","深色"},values={"system","light","dark"};
        String selectedMode=DisplayPreferences.theme(this);
        for(int i=0;i<labels.length;i++) {
            RadioButton option=new RadioButton(this);option.setId(View.generateViewId());
            option.setText(labels[i]);option.setTextSize(14);option.setTextColor(ui.ink);option.setMinHeight(ui.dp(48));
            option.setButtonTintList(new android.content.res.ColorStateList(
                    new int[][]{new int[]{android.R.attr.state_checked},new int[]{}},new int[]{ui.accent,ui.muted}));
            String value=values[i];option.setTag(value);modes.addView(option,new RadioGroup.LayoutParams(-1,-2));
            option.setChecked(value.equals(selectedMode));
        }
        modes.setOnCheckedChangeListener((group,id)-> {
            View selected=group.findViewById(id);if(selected==null)return;
            String value=(String)selected.getTag();if(value.equals(DisplayPreferences.theme(this)))return;
            applyAppearance(DisplayPreferences.setTheme(this,value));
        });
        page.addView(modes);page.addView(ui.space(8));
        Button font=ui.button("字体大小 · "+DisplayPreferences.FONT_LABELS[DisplayPreferences.fontIndex(this)],false,()-> {
            new AlertDialog.Builder(this).setTitle("字体大小")
                    .setSingleChoiceItems(DisplayPreferences.FONT_LABELS,DisplayPreferences.fontIndex(this),(dialog,which)-> {
                        dialog.dismiss();float scale=DisplayPreferences.FONT_SCALES[which];
                        if(scale!=DisplayPreferences.fontScale(this))applyAppearance(DisplayPreferences.setFontScale(this,scale));
                    }).setNegativeButton("取消",null).show();
        });font.setContentDescription("字体大小");page.addView(font,new LinearLayout.LayoutParams(-1,-2));
        TextView preview=ui.text("字体预览：把每一天安排得从容一些。",16,ui.ink,false);
        preview.setPadding(0,ui.dp(12),0,ui.dp(6));page.addView(preview);
        page.addView(ui.text("同时用于日历、编辑窗口和桌面小组件。仅影响这台手机。",12,ui.muted,false));page.addView(ui.space(22));
        page.addView(ui.text("设备同步",15,ui.ink,true));page.addView(ui.space(8));
        settingsStatus=ui.text(accountSummary(),14,ui.muted,false);settingsStatus.setLineSpacing(ui.dp(6),1);page.addView(settingsStatus);page.addView(ui.space(18));
        TextView keyLabel=ui.text("个人同步码",13,ui.ink,true);page.addView(keyLabel);page.addView(ui.space(6));
        EditText code=new EditText(this);code.setHint("粘贴你的同步码");code.setTextColor(ui.ink);code.setHintTextColor(ui.muted);code.setTextSize(14);
        code.setSingleLine(true);code.setInputType(InputType.TYPE_CLASS_TEXT|InputType.TYPE_TEXT_VARIATION_PASSWORD);code.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO);
        code.setSaveEnabled(false);code.setPadding(ui.dp(12),ui.dp(12),ui.dp(12),ui.dp(12));code.setBackground(ui.shape(ui.surface,12,true));page.addView(code,new LinearLayout.LayoutParams(-1,ui.dp(52)));
        TextView explain=ui.text("在自己的 Windows 和手机输入同一份同步码。朋友使用另一份。同步码相当于密码，请私下保存。",12,ui.muted,false);explain.setPadding(0,ui.dp(10),0,ui.dp(14));page.addView(explain);
        connectButton=ui.button("连接我的日历",true,()-> {
            String candidate=code.getText().toString().trim();if(candidate.isEmpty()){code.setError("请粘贴完整同步码");return;}
            int count=store.list().size();
            new AlertDialog.Builder(this).setTitle("连接你的个人日历？")
                .setMessage(count==0?"验证同步码后，将下载你在其他设备上的日程。":"本机现有 "+count+" 项日程会加入这个同步码对应的日历，并同步到你的其他设备。请确认使用的是你自己的同步码。")
                .setNegativeButton("取消",null).setPositiveButton("确认连接并同步",(d,w)-> {code.setText("");store.clearError();store.connect(candidate);SyncJobService.ensureScheduled(this);updateStatus();}).show();
        });page.addView(connectButton,new LinearLayout.LayoutParams(-1,ui.dp(50)));page.addView(ui.space(8));
        disconnectButton=ui.button("断开这台设备",false,()->new AlertDialog.Builder(this).setTitle("断开同步？")
            .setMessage("本机日程仍会保留。重新连接时请使用原来的个人同步码。")
            .setNegativeButton("取消",null).setPositiveButton("断开",(d,w)->{store.disconnect();SyncJobService.ensureScheduled(this);updateStatus();}).show());
        page.addView(disconnectButton,new LinearLayout.LayoutParams(-1,ui.dp(48)));page.addView(ui.space(16));
        TextView reminderTitle=ui.text("提醒与桌面",13,ui.ink,true);page.addView(reminderTitle);page.addView(ui.space(8));
        page.addView(ui.button("允许日程通知",false,()-> {
            if(Build.VERSION.SDK_INT>=33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED)requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},31);
            else startActivity(new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE,getPackageName()));
        }),new LinearLayout.LayoutParams(-1,ui.dp(48)));
        page.addView(ui.button("添加桌面小组件",false,()-> {
            android.appwidget.AppWidgetManager manager=android.appwidget.AppWidgetManager.getInstance(this);
            if(manager.isRequestPinAppWidgetSupported())manager.requestPinAppWidget(new android.content.ComponentName(this,AgendaWidgetProvider.class),null,null);
            else Toast.makeText(this,"请长按桌面 → 小组件 → 朝夕日历",Toast.LENGTH_LONG).show();
        }),new LinearLayout.LayoutParams(-1,ui.dp(48)));
        TextView widgets=ui.text("长按手机桌面 → 小组件 → 朝夕日历，可添加日程卡片。折叠、展开和分屏时，应用会按可用空间调整布局。",12,ui.muted,false);widgets.setPadding(0,ui.dp(12),0,0);page.addView(widgets);
        ScrollView scroll=scroll(page);
        settingsDialog=new AlertDialog.Builder(this).setTitle("设置").setView(scroll).setPositiveButton("完成",null).create();
        settingsDialog.setOnDismissListener(d->{settingsDialog=null;settingsStatus=null;connectButton=null;disconnectButton=null;});settingsDialog.show();updateStatus();
    }
    private void applyAppearance(boolean saved) {
        if(!saved){Toast.makeText(this,"设置暂时未保存，请重试",Toast.LENGTH_SHORT).show();return;}
        AgendaWidgetProvider.refreshAll(this);
        // Saved activity state restores the selected date, view, scroll and any editor draft.
        recreate();
    }
    @Override public void onRequestPermissionsResult(int requestCode,String[] permissions,int[] results){super.onRequestPermissionsResult(requestCode,permissions,results);if(requestCode==31){ReminderScheduler.reschedule(this);Toast.makeText(this,results.length>0&&results[0]==0?"通知已允许":"可稍后在系统设置中允许通知",Toast.LENGTH_SHORT).show();}}
}
