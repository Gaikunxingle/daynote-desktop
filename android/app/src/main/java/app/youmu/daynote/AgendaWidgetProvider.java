package app.youmu.daynote;

import android.app.PendingIntent;
import android.app.KeyguardManager;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.appwidget.AppWidgetProviderInfo;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.util.SizeF;
import android.util.TypedValue;
import android.view.View;
import android.view.ViewGroup;
import android.widget.RemoteViews;
import android.widget.Toast;

import java.time.LocalDate;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/** Home-screen-only widget, rendered immediately from the private local cache. */
public final class AgendaWidgetProvider extends AppWidgetProvider {
    public static final String ACTION_REFRESH = "app.youmu.daynote.REFRESH_WIDGET";
    public static final String ACTION_COMPLETE = "app.youmu.daynote.COMPLETE_WIDGET_TASK";
    private static final int[] ROWS = {R.id.widget_row_1, R.id.widget_row_2, R.id.widget_row_3,
            R.id.widget_row_4, R.id.widget_row_5};
    private static final int[] LABELS = {R.id.widget_label_1, R.id.widget_label_2, R.id.widget_label_3,
            R.id.widget_label_4, R.id.widget_label_5};
    private static final int[] COMPLETE = {R.id.widget_complete_1, R.id.widget_complete_2, R.id.widget_complete_3,
            R.id.widget_complete_4, R.id.widget_complete_5};
    private static final DateTimeFormatter DATE = DateTimeFormatter.ofPattern("M月d日 · EEEE", Locale.CHINA);
    private static final DateTimeFormatter CLOCK = DateTimeFormatter.ofPattern("HH:mm", Locale.CHINA);

    @Override public void onReceive(Context context, Intent intent) {
        if (intent == null) return;
        if (ACTION_COMPLETE.equals(intent.getAction())) {
            Context app = context.getApplicationContext();
            int widgetId = intent.getIntExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, AppWidgetManager.INVALID_APPWIDGET_ID);
            if (!canCompleteFromWidget(app, widgetId)) return;
            CalendarStore store = CalendarStore.get(app);
            if (completeTask(store, intent.getStringExtra("task_id"))) {
                // save() commits atomically before the card disappears. A receiver
                // must also arrange reminders/sync while no Activity is running.
                refreshAll(app);
                ReminderScheduler.reschedule(app);
                SyncJobService.requestNow(app);
            } else {
                refreshAll(app);
                Toast.makeText(app, "未能完成此事项，请打开朝夕查看。", Toast.LENGTH_SHORT).show();
            }
            return;
        }
        if (ACTION_REFRESH.equals(intent.getAction())) {
            SyncJobService.requestNow(context);
            refreshAll(context);
            return;
        }
        super.onReceive(context, intent);
    }

    private static boolean canCompleteFromWidget(Context context, int widgetId) {
        KeyguardManager keyguard = context.getSystemService(KeyguardManager.class);
        if (keyguard == null || keyguard.isKeyguardLocked()) return false;
        AppWidgetManager manager = AppWidgetManager.getInstance(context);
        int[] owned = manager.getAppWidgetIds(new ComponentName(context, AgendaWidgetProvider.class));
        for (int id : owned) {
            if (id == widgetId) return !isPrivateHost(manager.getAppWidgetOptions(id));
        }
        return false;
    }

    // Kept separate from host validation for isolated .qa tests. Always read the
    // current record: a stale card must not overwrite a newer title or time.
    static boolean completeTask(CalendarStore store, String taskId) {
        if (taskId == null || taskId.isEmpty()) return false;
        return store.setCompleted(taskId, true);
    }

    private static boolean isPrivateHost(Bundle options) {
        int category = options.getInt(AppWidgetManager.OPTION_APPWIDGET_HOST_CATEGORY,
                AppWidgetProviderInfo.WIDGET_CATEGORY_HOME_SCREEN);
        return (category & AppWidgetProviderInfo.WIDGET_CATEGORY_KEYGUARD) != 0
                || (category & AppWidgetProviderInfo.WIDGET_CATEGORY_HOME_SCREEN) == 0;
    }

    @Override public void onEnabled(Context context) {
        SyncJobService.ensureScheduled(context);
        SyncJobService.requestNow(context);
    }

    @Override public void onUpdate(Context context, AppWidgetManager manager, int[] appWidgetIds) {
        update(context, manager, appWidgetIds);
        ReminderScheduler.reschedule(context);
        // System widget refresh also advances the local date while offline.
        // Keep one periodic network job; do not turn every local refresh into
        // another immediate cloud request.
        SyncJobService.ensureScheduled(context);
    }

    @Override public void onAppWidgetOptionsChanged(Context context, AppWidgetManager manager, int id, Bundle options) {
        update(context, manager, new int[]{id});
    }

    public static void refreshAll(Context context) {
        Context app = context.getApplicationContext();
        AppWidgetManager manager = AppWidgetManager.getInstance(app);
        int[] ids = manager.getAppWidgetIds(new ComponentName(app, AgendaWidgetProvider.class));
        if (ids.length != 0) update(app, manager, ids);
    }

    /** Two fixed examples, rendered with the actual widget resources. No store,
     * credentials, background jobs or PendingIntents are accessed or created. */
    public static View preview(Context context, ViewGroup parent) {
        Context app = context.getApplicationContext();
        Metrics metrics = new Metrics(app);
        LocalDate exampleDate = LocalDate.of(2026, 1, 1);
        List<SyncCore.Record> examples = new ArrayList<>();
        examples.add(example("preview-reading", "阅读", exampleDate, 9));
        examples.add(example("preview-walk", "散步", exampleDate, 18));
        RemoteViews views = render(app, 0, R.layout.widget_agenda_compact, 2, true,
                examples, false, metrics, exampleDate, false, false, false);
        views.setTextViewText(R.id.widget_date, "今天 · 示例");
        views.setTextViewText(R.id.widget_status, "字号预览");
        View result = views.apply(app, parent);
        result.setLayoutParams(new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,
                metrics.pixels(metrics.height(2, true))));
        makePreviewInert(result);
        return result;
    }

    private static SyncCore.Record example(String id, String title, LocalDate date, int hour) {
        SyncCore.Record record = new SyncCore.Record();
        record.id = id;
        record.data.title = title;
        record.data.dueAt = date.atTime(hour, 0).atZone(ZoneId.systemDefault()).toInstant().toString();
        record.data.hasTime = true;
        return record;
    }

    private static void makePreviewInert(View view) {
        view.setOnClickListener(null);
        view.setOnLongClickListener(null);
        view.setClickable(false);
        view.setLongClickable(false);
        view.setFocusable(false);
        if (view instanceof ViewGroup) {
            ViewGroup group = (ViewGroup)view;
            for (int i = 0; i < group.getChildCount(); i++) makePreviewInert(group.getChildAt(i));
        }
    }

    private static void update(Context context, AppWidgetManager manager, int[] ids) {
        // Activity resources already include its preference. Widgets instead use
        // application/system resources and multiply the local preference once.
        context = context.getApplicationContext();
        CalendarStore store = CalendarStore.get(context);
        Metrics metrics = new Metrics(context);
        List<SyncCore.Record> records = relevant(store.list(), LocalDate.now());
        for (int id : ids) {
            Bundle options = manager.getAppWidgetOptions(id);
            boolean privateHost = isPrivateHost(options);
            List<SyncCore.Record> displayed = privateHost ? java.util.Collections.emptyList() : records;
            RemoteViews views;
            if (Build.VERSION.SDK_INT >= 31) {
                Map<SizeF, RemoteViews> layouts = new LinkedHashMap<>();
                for (int width : new int[]{160, 300}) {
                    boolean wide = width == 300;
                    int smallLayout = wide ? R.layout.widget_agenda_wide : R.layout.widget_agenda_compact;
                    layouts.put(new SizeF(width, metrics.height(1, false)),
                            build(context, id, smallLayout, 1, false, displayed, store, privateHost, metrics));
                    for (int count = 1; count <= ROWS.length; count++) {
                        int layout = wide ? R.layout.widget_agenda_wide :
                                count >= 3 ? R.layout.widget_agenda_tall : R.layout.widget_agenda_compact;
                        layouts.put(new SizeF(width, metrics.height(count, true)),
                                build(context, id, layout, count, true, displayed, store, privateHost, metrics));
                    }
                }
                views = new RemoteViews(layouts);
            } else {
                int width = options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH, 180);
                int height = options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT, 140);
                boolean details = height >= metrics.height(1, true);
                int count = 1;
                while (count < ROWS.length && height >= metrics.height(count + 1, details)) count++;
                int layout = width >= 300 ? R.layout.widget_agenda_wide :
                        count >= 3 ? R.layout.widget_agenda_tall : R.layout.widget_agenda_compact;
                views = build(context, id, layout, count, details, displayed, store, privateHost, metrics);
            }
            manager.updateAppWidget(id, views);
        }
    }

    private static List<SyncCore.Record> relevant(List<SyncCore.Record> source, LocalDate today) {
        List<SyncCore.Record> result = new ArrayList<>();
        for (SyncCore.Record record : source) {
            if (record.data == null || record.data.completed) continue;
            try {
                LocalDate date = record.data.dueDate();
                if (date != null && (!date.isBefore(today) || record.data.isDeadline)) result.add(record);
            } catch (RuntimeException ignored) { }
        }
        result.sort(Comparator.comparing((SyncCore.Record r) -> r.data.dueDate())
                .thenComparing(r -> r.data.hasTime ? r.data.dueInstant() :
                        r.data.dueDate().atStartOfDay(ZoneId.systemDefault()).toInstant())
                .thenComparing(r -> r.id));
        return result;
    }

    private static RemoteViews build(Context context, int widgetId, int layout, int rowCount, boolean details,
                                     List<SyncCore.Record> records, CalendarStore store, boolean privateHost, Metrics metrics) {
        return render(context.getApplicationContext(), widgetId, layout, rowCount, details, records,
                privateHost, metrics, LocalDate.now(), store.busy(), store.connected(), true);
    }

    private static RemoteViews render(Context context, int widgetId, int layout, int rowCount, boolean details,
                                      List<SyncCore.Record> records, boolean privateHost, Metrics metrics,
                                      LocalDate today, boolean busy, boolean connected, boolean interactive) {
        RemoteViews views = new RemoteViews(context.getPackageName(), layout);
        applyAppearance(views, layout, metrics);
        views.setViewVisibility(R.id.widget_date, details ? View.VISIBLE : View.GONE);
        views.setViewVisibility(R.id.widget_status, details ? View.VISIBLE : View.GONE);
        views.setTextViewText(R.id.widget_date, today.format(DATE));
        if (layout == R.layout.widget_agenda_wide) {
            // A one-row card spends its height on the 44dp completion target.
            views.setViewVisibility(R.id.widget_day_panel, details && rowCount >= 2 ? View.VISIBLE : View.GONE);
            views.setTextViewText(R.id.widget_day_number, String.valueOf(today.getDayOfMonth()));
            views.setTextViewText(R.id.widget_month_label, today.getMonthValue() + "月");
        }
        if (interactive) {
            views.setOnClickPendingIntent(R.id.widget_root, open(context, widgetId, null, false));
            views.setOnClickPendingIntent(R.id.widget_add, open(context, widgetId, null, true));
            views.setOnClickPendingIntent(R.id.widget_refresh, refresh(context, widgetId));
        }
        for (int i = 0; i < ROWS.length; i++) {
            if (i >= rowCount || i >= records.size()) {
                views.setViewVisibility(ROWS[i], View.GONE);
                views.setTextViewText(LABELS[i], "");
            } else {
                SyncCore.Record record = records.get(i);
                String label = rowLabel(record, today);
                views.setViewVisibility(ROWS[i], View.VISIBLE);
                views.setTextViewText(LABELS[i], label);
                if (interactive) {
                    views.setOnClickPendingIntent(LABELS[i], open(context, widgetId, record, false));
                    views.setOnClickPendingIntent(COMPLETE[i], complete(context, widgetId, record.id));
                }
                views.setContentDescription(LABELS[i], label);
                views.setContentDescription(COMPLETE[i], interactive ? "完成：" + record.data.title : "完成圆圈示例");
            }
        }
        boolean empty = records.isEmpty();
        views.setViewVisibility(R.id.widget_empty, empty ? View.VISIBLE : View.GONE);
        views.setTextViewText(R.id.widget_empty, privateHost ? "解锁并打开朝夕查看日程" : "近日没有待办日程");
        String footer;
        if (privateHost) footer = "锁屏不显示个人日程";
        else if (busy) footer = "正在同步…";
        else if (records.size() > rowCount) footer = "还有 " + (records.size() - rowCount) + " 项 · 点开查看";
        else footer = connected ? "本机日程 · 点开查看同步状态" : "本机日程 · 尚未连接同步";
        views.setTextViewText(R.id.widget_status, footer);
        return views;
    }

    private static void applyAppearance(RemoteViews views, int layout, Metrics metrics) {
        int text = metrics.dark ? 0xFFEDF3F8 : 0xFF273D4F;
        int secondary = metrics.dark ? 0xFFAFBDCC : 0xFF647584;
        int accent = metrics.dark ? 0xFFF0A07F : 0xFFB34F38;
        views.setInt(R.id.widget_root, "setBackgroundResource", metrics.dark ?
                R.drawable.widget_background_dark : R.drawable.widget_background);
        setTextStyle(views, R.id.widget_title, 18, text, metrics);
        setTextStyle(views, R.id.widget_date, 11, secondary, metrics);
        setTextStyle(views, R.id.widget_status, 11, secondary, metrics);
        setTextStyle(views, R.id.widget_empty, 13, secondary, metrics);
        views.setInt(R.id.widget_header, "setMinimumHeight", metrics.pixels(metrics.header));
        views.setInt(R.id.widget_date, "setMinimumHeight", metrics.pixels(metrics.meta));
        views.setInt(R.id.widget_status, "setMinimumHeight", metrics.pixels(metrics.meta));
        for (int action : new int[]{R.id.widget_refresh, R.id.widget_add}) {
            views.setTextColor(action, action == R.id.widget_add ? accent : secondary);
            // These glyphs are icons; their tap area is independent of font size.
            views.setTextViewTextSize(action, TypedValue.COMPLEX_UNIT_DIP, 22);
            views.setInt(action, "setBackgroundResource", metrics.dark ?
                    R.drawable.widget_action_background_dark : R.drawable.widget_action_background);
        }
        for (int i = 0; i < ROWS.length; i++) {
            views.setInt(ROWS[i], "setMinimumHeight", metrics.pixels(metrics.row));
            setTextStyle(views, LABELS[i], 13, text, metrics);
            views.setImageViewResource(COMPLETE[i], metrics.dark ?
                    R.drawable.widget_complete_circle_dark : R.drawable.widget_complete_circle);
        }
        if (layout == R.layout.widget_agenda_wide) {
            setTextStyle(views, R.id.widget_day_number, 30, accent, metrics);
            setTextStyle(views, R.id.widget_month_label, 11, secondary, metrics);
        }
    }

    private static void setTextStyle(RemoteViews views, int id, float baseSp, int color, Metrics metrics) {
        views.setTextColor(id, color);
        views.setTextViewTextSize(id, TypedValue.COMPLEX_UNIT_SP, baseSp * metrics.fontScale);
    }

    private static final class Metrics {
        final boolean dark;
        final float fontScale;
        final float density;
        final int header, meta, row;
        Metrics(Context context) {
            context = context.getApplicationContext();
            dark = DisplayPreferences.isDark(context);
            float chosen = DisplayPreferences.widgetFontScale(context);
            fontScale = Float.isFinite(chosen) && chosen > 0 ? chosen : 1;
            density = context.getResources().getDisplayMetrics().density;
            header = Math.max(32, textDp(context, 18) + 8);
            meta = Math.max(18, textDp(context, 11) + 6);
            row = Math.max(44, textDp(context, 13) + 16);
        }
        int textDp(Context context, float sp) {
            return (int)Math.ceil(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_SP,
                    sp * fontScale, context.getResources().getDisplayMetrics()) / density);
        }
        int pixels(int dp) { return (int)Math.ceil(dp * density); }
        int height(int rows, boolean details) { return 20 + header + row * rows + (details ? 2 * meta : 0); }
    }

    private static String rowLabel(SyncCore.Record record, LocalDate today) {
        LocalDate date = record.data.dueDate();
        String prefix;
        if (record.data.isDeadline && date.isBefore(today)) prefix = "逾期";
        else if (date.equals(today)) prefix = record.data.hasTime ?
                record.data.dueInstant().atZone(ZoneId.systemDefault()).format(CLOCK) : "全天";
        else {
            prefix = date.equals(today.plusDays(1)) ? "明天" : date.getMonthValue() + "/" + date.getDayOfMonth();
            if (record.data.hasTime) prefix += " " + record.data.dueInstant().atZone(ZoneId.systemDefault()).format(CLOCK);
        }
        if (record.data.isDeadline && !date.isBefore(today)) prefix += " 截止";
        return prefix + "  " + record.data.title;
    }

    private static PendingIntent open(Context context, int widgetId, SyncCore.Record record, boolean create) {
        Intent intent = new Intent(context, MainActivity.class)
                .setAction(create ? "app.youmu.daynote.NEW_TASK" : record == null ? Intent.ACTION_MAIN : "app.youmu.daynote.OPEN_TASK")
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        String key = create ? "new" : record == null ? "home" : record.id;
        intent.setData(new Uri.Builder().scheme("daynote").authority("widget")
                .appendPath(String.valueOf(widgetId)).appendPath(key).build());
        if (record != null) intent.putExtra("task_id", record.id);
        if (create) intent.putExtra("date", LocalDate.now().toString());
        return PendingIntent.getActivity(context, 0, intent, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }

    private static PendingIntent refresh(Context context, int widgetId) {
        Intent intent = new Intent(context, AgendaWidgetProvider.class).setAction(ACTION_REFRESH)
                .setData(new Uri.Builder().scheme("daynote").authority("widget-refresh")
                        .appendPath(String.valueOf(widgetId)).build());
        return PendingIntent.getBroadcast(context, 0, intent, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }

    private static PendingIntent complete(Context context, int widgetId, String taskId) {
        Intent intent = new Intent(context, AgendaWidgetProvider.class).setAction(ACTION_COMPLETE)
                .setData(new Uri.Builder().scheme("daynote").authority("widget-complete")
                        .appendPath(String.valueOf(widgetId)).appendPath(taskId).build())
                .putExtra(AppWidgetManager.EXTRA_APPWIDGET_ID, widgetId).putExtra("task_id", taskId);
        return PendingIntent.getBroadcast(context, 0, intent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }
}
