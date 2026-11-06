package app.youmu.daynote;

import android.app.AlertDialog;
import android.app.DatePickerDialog;
import android.app.Dialog;
import android.app.TimePickerDialog;
import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.os.Bundle;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.Switch;
import android.widget.TextView;
import java.time.LocalDate;
import java.time.LocalTime;
import java.time.ZoneId;
import java.time.ZonedDateTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/** Keeps an independent draft, including its base revision, while the store continues syncing. */
public final class TaskEditorDialog extends Dialog {
    public interface SaveHandler { boolean save(String id, SyncCore.TaskData data, long expectedRevision); }
    private final DaynoteUi ui;
    private final SaveHandler onSave;
    private String id;
    private long revision = -1;
    private SyncCore.TaskData initial;
    private boolean scheduled, allDay, hasEnd;
    private LocalDate startDate, endDate;
    private LocalTime startTime, endTime;
    private String colorId;
    private int reminder;
    private EditText title, notes;
    private Switch scheduleSwitch, allDaySwitch, endSwitch, deadlineSwitch, completeSwitch;
    private LinearLayout timeSection, endRow;
    private Button dateButton, timeButton, endDateButton, endTimeButton;
    private Spinner reminderPicker;
    private final List<Integer> reminderValues = new ArrayList<>();
    private TextView error, allDayHint;
    private Bundle restoredDraft;
    // Kept as Object so API 26-32 never need to resolve the API 33 callback type.
    private Object modernBackCallback;
    private final List<Button> colorButtons = new ArrayList<>();
    private static final String[] COLORS = {"blue", "teal", "purple", "orange", "green", "red", "pink", "gray"};
    private static final String[] COLOR_NAMES = {"蓝色", "青色", "紫色", "橙色", "绿色", "红色", "粉色", "灰色"};

    public TaskEditorDialog(Context context, SyncCore.Record record, LocalDate date, Bundle draft, SaveHandler handler) {
        super(context, (context.getResources().getConfiguration().uiMode & 0x30) == 0x20
                ? android.R.style.Theme_Material_Dialog_NoActionBar : android.R.style.Theme_Material_Light_Dialog_NoActionBar);
        ui = new DaynoteUi(context); onSave = handler;
        initial = record == null ? new SyncCore.TaskData() : record.data.copy();
        id = record == null ? null : record.id; revision = record == null ? -1 : record.revision;
        LocalDate today = date == null ? LocalDate.now() : date;
        ZonedDateTime now = ZonedDateTime.now().withSecond(0).withNano(0);
        startDate = record != null && record.data.dueDate() != null ? record.data.dueDate() : today;
        startTime = record != null && initial.hasTime && initial.dueInstant() != null
                ? initial.dueInstant().atZone(ZoneId.systemDefault()).toLocalTime() : now.toLocalTime();
        java.time.LocalDateTime defaultEnd = startDate.atTime(startTime).plusHours(1);
        endDate = defaultEnd.toLocalDate(); endTime = defaultEnd.toLocalTime();
        scheduled = record == null ? date != null : initial.dueAt != null;
        allDay = !initial.hasTime; hasEnd = initial.endAt != null;
        if (hasEnd) {
            try {
                if (allDay) endDate = LocalDate.parse(initial.endAt);
                else {
                    ZonedDateTime z = java.time.OffsetDateTime.parse(initial.endAt).atZoneSameInstant(ZoneId.systemDefault());
                    endDate = z.toLocalDate(); endTime = z.toLocalTime();
                }
            } catch (RuntimeException ignored) { hasEnd = false; }
        }
        colorId = initial.colorId; reminder = initial.reminderMinutes;
        if (draft != null) restoreDraft(draft);
        setCanceledOnTouchOutside(false);
    }

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        LinearLayout shell = ui.column(); shell.setBackground(ui.shape(ui.background, 22, false));
        shell.setPadding(ui.dp(20), ui.dp(16), ui.dp(20), ui.dp(12));
        LinearLayout top = ui.row();
        TextView heading = ui.text(id == null ? "记下一件事" : "编辑日程", 23, ui.ink, true);
        top.addView(heading, new LinearLayout.LayoutParams(0, ui.dp(52), 1)); heading.setGravity(Gravity.CENTER_VERTICAL);
        top.addView(ui.button("取消", false, this::askDismiss)); shell.addView(top);
        ScrollView scroll = new ScrollView(getContext()); scroll.setFillViewport(false);
        LinearLayout content = ui.column(); content.setPadding(0, ui.dp(8), 0, ui.dp(14)); scroll.addView(content);
        shell.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        title = edit("这件事叫什么？", false); title.setText(restoredDraft == null ? initial.title : restoredDraft.getString("title", ""));
        title.setTextSize(19); content.addView(title); content.addView(ui.space(10));
        notes = edit("补充地点、准备事项或备注…", true); notes.setText(restoredDraft == null ? initial.notes : restoredDraft.getString("notes", ""));
        content.addView(notes); content.addView(ui.space(18));
        content.addView(section("时间安排"));
        scheduleSwitch = toggle("设置日期", scheduled); content.addView(scheduleSwitch);
        timeSection = ui.column(); content.addView(timeSection);
        allDaySwitch = toggle("全天日程", allDay); timeSection.addView(allDaySwitch);
        LinearLayout startRow = ui.row();
        dateButton = ui.button("", false, () -> pickDate(false)); timeButton = ui.button("", false, () -> pickTime(false));
        addDateRow(startRow, dateButton, timeButton); timeSection.addView(startRow);
        endSwitch = toggle("设置结束时间", hasEnd); timeSection.addView(endSwitch);
        endRow = ui.row(); endDateButton = ui.button("", false, () -> pickDate(true)); endTimeButton = ui.button("", false, () -> pickTime(true));
        addDateRow(endRow, endDateButton, endTimeButton); timeSection.addView(endRow);
        timeSection.addView(ui.space(8)); timeSection.addView(section("提醒"));
        reminderPicker = new Spinner(getContext()); reminderPicker.setMinimumHeight(ui.dp(48));
        String[] labels = {"不提醒", "到时提醒", "提前 5 分钟", "提前 10 分钟", "提前 15 分钟", "提前 30 分钟", "提前 1 小时", "提前 1 天"};
        int[] values = {-1,0,5,10,15,30,60,1440}; List<String> options = new ArrayList<>();
        for (int i = 0; i < values.length; i++) { reminderValues.add(values[i]); options.add(labels[i]); }
        if (!reminderValues.contains(reminder)) { reminderValues.add(reminder); options.add("提前 " + reminder + " 分钟"); }
        ArrayAdapter<String> adapter = new ArrayAdapter<>(getContext(), android.R.layout.simple_spinner_dropdown_item, options);
        reminderPicker.setAdapter(adapter); reminderPicker.setSelection(reminderValues.indexOf(reminder)); timeSection.addView(reminderPicker);
        allDayHint = ui.text("全天事项以当天 09:00 计算提醒时间。", 12, ui.muted, false); timeSection.addView(allDayHint);
        content.addView(ui.space(18)); content.addView(section("分类与状态"));
        deadlineSwitch = toggle("这是一个截止事项", restoredDraft == null ? initial.isDeadline : restoredDraft.getBoolean("deadline")); content.addView(deadlineSwitch);
        completeSwitch = toggle("已完成", restoredDraft == null ? initial.completed : restoredDraft.getBoolean("completed")); content.addView(completeSwitch);
        content.addView(ui.space(12)); content.addView(section("标记颜色"));
        HorizontalScrollView colorScroll = new HorizontalScrollView(getContext()); colorScroll.setHorizontalScrollBarEnabled(false);
        LinearLayout colorRow = ui.row();
        for (int i = 0; i < COLORS.length; i++) {
            final String color = COLORS[i];
            Button b = ui.button("●", false, () -> { colorId = color; updateColors(); }); b.setTextSize(25); b.setTextColor(ui.color(color));
            b.setPadding(0,0,0,0); b.setContentDescription(COLOR_NAMES[i]);
            colorButtons.add(b); colorRow.addView(b, new LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)));
        }
        colorScroll.addView(colorRow); content.addView(colorScroll);
        error = ui.text("", 13, ui.accent, false); error.setPadding(0,ui.dp(12),0,0); content.addView(error);
        Button save = ui.button("保存日程", true, this::save);
        shell.addView(save, new LinearLayout.LayoutParams(-1, ui.dp(52)));
        setContentView(shell);
        scheduleSwitch.setOnCheckedChangeListener((b, v) -> { scheduled = v; updateTime(); });
        allDaySwitch.setOnCheckedChangeListener((b, v) -> { allDay = v; updateTime(); });
        endSwitch.setOnCheckedChangeListener((b, v) -> { hasEnd = v; updateTime(); });
        updateTime(); updateColors();
        Window window = getWindow();
        if (window != null) {
            window.setBackgroundDrawableResource(android.R.color.transparent);
            window.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE | WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_HIDDEN);
            resizeForWindow();
        }
    }
    @Override public void show() { super.show(); resizeForWindow(); }
    @Override protected void onStart() {
        super.onStart();
        if(android.os.Build.VERSION.SDK_INT>=33 && modernBackCallback==null) {
            modernBackCallback=Api33Back.register(this,this::askDismiss);
        }
    }
    @Override protected void onStop() {
        if(android.os.Build.VERSION.SDK_INT>=33 && modernBackCallback!=null) {
            Api33Back.unregister(this,modernBackCallback);modernBackCallback=null;
        }
        super.onStop();
    }
    public void resizeForWindow() {
        Window w=getWindow();if(w==null)return;
        int width=getContext().getResources().getDisplayMetrics().widthPixels;
        if(android.os.Build.VERSION.SDK_INT>=30){WindowManager manager=getContext().getSystemService(WindowManager.class);if(manager!=null)width=manager.getCurrentWindowMetrics().getBounds().width();}
        w.setLayout(Math.max(ui.dp(240),Math.min(width-ui.dp(16),ui.dp(640))),-1);
    }
    private EditText edit(String hint, boolean multi) {
        EditText e = new EditText(getContext()); e.setHint(hint); e.setTextColor(ui.ink); e.setHintTextColor(ui.muted); e.setTextSize(15);
        e.setPadding(ui.dp(14), ui.dp(14), ui.dp(14), ui.dp(14)); e.setMinimumHeight(ui.dp(52));
        e.setBackground(ui.shape(ui.surface, 14, true));
        if (multi) { e.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE | InputType.TYPE_TEXT_FLAG_CAP_SENTENCES); e.setMinLines(3); e.setMaxLines(7); e.setGravity(Gravity.TOP); }
        else { e.setSingleLine(true); e.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_SENTENCES); }
        return e;
    }
    private TextView section(String text) { TextView t = ui.text(text, 12, ui.muted, true); t.setPadding(0,0,0,ui.dp(4)); return t; }
    private Switch toggle(String label, boolean checked) {
        Switch s = new Switch(getContext()); s.setText(label); s.setTextColor(ui.ink); s.setTextSize(15); s.setMinHeight(ui.dp(52));
        s.setChecked(checked); s.setPadding(ui.dp(2),0,ui.dp(4),0);
        s.setThumbTintList(new ColorStateList(new int[][]{new int[]{android.R.attr.state_checked},new int[]{}}, new int[]{ui.accent,ui.muted}));
        return s;
    }
    private void addDateRow(LinearLayout row, Button date, Button time) {
        LinearLayout.LayoutParams d = new LinearLayout.LayoutParams(0,ui.dp(50),1); d.setMargins(0,0,ui.dp(8),0); row.addView(date,d);
        row.addView(time,new LinearLayout.LayoutParams(ui.dp(100),ui.dp(50)));
    }
    private void updateTime() {
        if (timeSection == null) return;
        timeSection.setVisibility(scheduled ? View.VISIBLE : View.GONE); endRow.setVisibility(hasEnd ? View.VISIBLE : View.GONE);
        timeButton.setVisibility(allDay ? View.GONE : View.VISIBLE); endTimeButton.setVisibility(allDay ? View.GONE : View.VISIBLE);
        dateButton.setText("开始 · " + startDate.format(DateTimeFormatter.ofPattern("M月d日 E", Locale.CHINA)));
        endDateButton.setText("结束 · " + endDate.format(DateTimeFormatter.ofPattern("M月d日 E", Locale.CHINA)));
        timeButton.setText(startTime.format(DateTimeFormatter.ofPattern("HH:mm"))); endTimeButton.setText(endTime.format(DateTimeFormatter.ofPattern("HH:mm")));
        allDayHint.setVisibility(allDay ? View.VISIBLE : View.GONE);
    }
    private void pickDate(boolean end) {
        LocalDate d = end ? endDate : startDate;
        new DatePickerDialog(getContext(), (picker, y, m, day) -> {
            LocalDate next = LocalDate.of(y,m+1,day);
            if (end) endDate = next; else { startDate = next; if (endDate.isBefore(next)) endDate = next; }
            updateTime();
        },d.getYear(),d.getMonthValue()-1,d.getDayOfMonth()).show();
    }
    private void pickTime(boolean end) {
        LocalTime t = end ? endTime : startTime;
        new TimePickerDialog(getContext(), (picker,h,m) -> { if (end) endTime = LocalTime.of(h,m); else startTime = LocalTime.of(h,m); updateTime(); },t.getHour(),t.getMinute(),true).show();
    }
    private void updateColors() {
        for (int i=0;i<colorButtons.size();i++) {
            Button b=colorButtons.get(i); boolean selected=COLORS[i].equals(colorId);
            b.setText(selected ? "✓" : "●"); b.setBackground(ui.ripple(selected ? ui.softAccent : Color.TRANSPARENT,12));
            b.setSelected(selected); b.setContentDescription(COLOR_NAMES[i]+(selected ? "，已选中" : ""));
        }
    }
    private void save() {
        SyncCore.TaskData d=initial.copy(); d.title=title.getText().toString().trim(); d.notes=notes.getText().toString();
        d.hasTime=!allDay; d.completed=completeSwitch.isChecked(); d.isDeadline=deadlineSwitch.isChecked(); d.colorId=colorId;
        d.reminderMinutes=scheduled ? reminderValues.get(reminderPicker.getSelectedItemPosition()) : -1;
        d.dueAt=scheduled ? wire(startDate,startTime) : null; d.endAt=scheduled && hasEnd ? wire(endDate,endTime) : null;
        if(d.title.isEmpty()){error.setText("先给这件事起个名字。");title.requestFocus();return;}
        if(d.title.codePointCount(0,d.title.length())>200 || d.notes.codePointCount(0,d.notes.length())>20000){error.setText("标题最多 200 个字符，备注最多 20000 个字符。");return;}
        if(scheduled && hasEnd && (allDay ? endDate.isBefore(startDate) : endDate.atTime(endTime).isBefore(startDate.atTime(startTime)))){error.setText("结束时间不能早于开始时间。");return;}
        if(onSave.save(id,d,revision)) dismiss();
        else { String message=CalendarStore.get(getContext()).error(); error.setText(message == null || message.isEmpty() ? "暂时无法保存，请重试。" : message); }
    }
    private String wire(LocalDate date, LocalTime time) {
        return allDay ? date.toString() : date.atTime(time.withSecond(0).withNano(0)).atZone(ZoneId.systemDefault()).toInstant().toString();
    }
    public Bundle saveDraft() {
        Bundle b = new Bundle(); b.putString("id",id); b.putLong("revision",revision);
        b.putString("title",title==null?initial.title:title.getText().toString()); b.putString("notes",notes==null?initial.notes:notes.getText().toString());
        b.putBoolean("scheduled",scheduled); b.putBoolean("allDay",allDay); b.putBoolean("hasEnd",hasEnd);
        b.putString("startDate",startDate.toString()); b.putString("endDate",endDate.toString()); b.putString("startTime",startTime.toString()); b.putString("endTime",endTime.toString());
        b.putString("color",colorId); b.putInt("reminder",reminderPicker==null?reminder:reminderValues.get(reminderPicker.getSelectedItemPosition()));
        b.putBoolean("deadline",deadlineSwitch==null?initial.isDeadline:deadlineSwitch.isChecked()); b.putBoolean("completed",completeSwitch==null?initial.completed:completeSwitch.isChecked());
        return b;
    }
    private void restoreDraft(Bundle b) {
        restoredDraft=new Bundle(b); id=b.getString("id"); revision=b.getLong("revision",-1);
        scheduled=b.getBoolean("scheduled"); allDay=b.getBoolean("allDay"); hasEnd=b.getBoolean("hasEnd");
        try { startDate=LocalDate.parse(b.getString("startDate")); endDate=LocalDate.parse(b.getString("endDate")); startTime=LocalTime.parse(b.getString("startTime")); endTime=LocalTime.parse(b.getString("endTime")); } catch(RuntimeException ignored) {}
        colorId=b.getString("color","blue"); reminder=b.getInt("reminder",-1);
    }
    private void askDismiss() {
        new AlertDialog.Builder(getContext()).setTitle("放弃这次编辑？").setMessage("还没有保存的内容将被丢弃。")
                .setNegativeButton("继续编辑",null).setPositiveButton("放弃",(d,w)->dismiss()).show();
    }
    // Android 13+ gestures use the lifecycle-registered callback above. This override
    // exists only for the platform Dialog back dispatch on Android 8-12L.
    @android.annotation.SuppressLint("GestureBackNavigation")
    @Override public void onBackPressed() {
        if(android.os.Build.VERSION.SDK_INT<33)askDismiss();
    }
    @android.annotation.TargetApi(33)
    private static final class Api33Back {
        static Object register(Dialog dialog,Runnable action) {
            android.window.OnBackInvokedCallback callback=action::run;
            dialog.getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                    android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT,callback);
            return callback;
        }
        static void unregister(Dialog dialog,Object callback) {
            dialog.getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback(
                    (android.window.OnBackInvokedCallback)callback);
        }
    }
}
