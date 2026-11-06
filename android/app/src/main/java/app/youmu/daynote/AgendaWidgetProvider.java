package app.youmu.daynote;

import android.app.PendingIntent;
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
import android.view.View;
import android.widget.RemoteViews;

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
    private static final int[] ROWS = {R.id.widget_row_1, R.id.widget_row_2, R.id.widget_row_3,
            R.id.widget_row_4, R.id.widget_row_5};
    private static final DateTimeFormatter DATE = DateTimeFormatter.ofPattern("M月d日 · EEEE", Locale.CHINA);
    private static final DateTimeFormatter CLOCK = DateTimeFormatter.ofPattern("HH:mm", Locale.CHINA);

    @Override public void onReceive(Context context, Intent intent) {
        if (ACTION_REFRESH.equals(intent.getAction())) {
            SyncJobService.requestNow(context);
            refreshAll(context);
            return;
        }
        super.onReceive(context, intent);
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

    private static void update(Context context, AppWidgetManager manager, int[] ids) {
        CalendarStore store = CalendarStore.get(context);
        List<SyncCore.Record> records = relevant(store.list(), LocalDate.now());
        for (int id : ids) {
            Bundle options = manager.getAppWidgetOptions(id);
            boolean privateHost = (options.getInt(AppWidgetManager.OPTION_APPWIDGET_HOST_CATEGORY,
                    AppWidgetProviderInfo.WIDGET_CATEGORY_HOME_SCREEN) & AppWidgetProviderInfo.WIDGET_CATEGORY_KEYGUARD) != 0;
            List<SyncCore.Record> displayed = privateHost ? java.util.Collections.emptyList() : records;
            RemoteViews views;
            if (Build.VERSION.SDK_INT >= 31) {
                Map<SizeF, RemoteViews> layouts = new LinkedHashMap<>();
                layouts.put(new SizeF(160, 140), build(context, id, R.layout.widget_agenda_compact, 2, displayed, store, privateHost));
                layouts.put(new SizeF(300, 140), build(context, id, R.layout.widget_agenda_wide, 3, displayed, store, privateHost));
                layouts.put(new SizeF(160, 250), build(context, id, R.layout.widget_agenda_tall, 5, displayed, store, privateHost));
                views = new RemoteViews(layouts);
            } else {
                int width = options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_WIDTH, 180);
                int height = options.getInt(AppWidgetManager.OPTION_APPWIDGET_MIN_HEIGHT, 140);
                int layout = height >= 250 ? R.layout.widget_agenda_tall : width >= 300 ? R.layout.widget_agenda_wide : R.layout.widget_agenda_compact;
                views = build(context, id, layout, height >= 250 ? 5 : width >= 300 ? 3 : 2, displayed, store, privateHost);
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

    private static RemoteViews build(Context context, int widgetId, int layout, int rowCount,
                                     List<SyncCore.Record> records, CalendarStore store, boolean privateHost) {
        RemoteViews views = new RemoteViews(context.getPackageName(), layout);
        LocalDate today = LocalDate.now();
        views.setTextViewText(R.id.widget_date, today.format(DATE));
        if (layout == R.layout.widget_agenda_wide) {
            views.setTextViewText(R.id.widget_day_number, String.valueOf(today.getDayOfMonth()));
            views.setTextViewText(R.id.widget_month_label, today.getMonthValue() + "月");
        }
        views.setOnClickPendingIntent(R.id.widget_root, open(context, widgetId, null, false));
        views.setOnClickPendingIntent(R.id.widget_add, open(context, widgetId, null, true));
        views.setOnClickPendingIntent(R.id.widget_refresh, refresh(context, widgetId));
        for (int i = 0; i < rowCount; i++) {
            if (i >= records.size()) {
                views.setViewVisibility(ROWS[i], View.GONE);
                views.setTextViewText(ROWS[i], "");
            } else {
                SyncCore.Record record = records.get(i);
                views.setViewVisibility(ROWS[i], View.VISIBLE);
                views.setTextViewText(ROWS[i], rowLabel(record, today));
                views.setOnClickPendingIntent(ROWS[i], open(context, widgetId, record, false));
                views.setContentDescription(ROWS[i], rowLabel(record, today));
            }
        }
        boolean empty = records.isEmpty();
        views.setViewVisibility(R.id.widget_empty, empty ? View.VISIBLE : View.GONE);
        views.setTextViewText(R.id.widget_empty, privateHost ? "解锁并打开朝夕查看日程" : "近日没有待办日程\n点 ＋ 记下一件事");
        String footer;
        if (privateHost) footer = "锁屏不显示个人日程";
        else if (store.busy()) footer = "正在同步…";
        else if (records.size() > rowCount) footer = "还有 " + (records.size() - rowCount) + " 项 · 点开查看";
        else footer = store.connected() ? "本机日程 · 点开查看同步状态" : "本机日程 · 尚未连接同步";
        views.setTextViewText(R.id.widget_status, footer);
        return views;
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
}
