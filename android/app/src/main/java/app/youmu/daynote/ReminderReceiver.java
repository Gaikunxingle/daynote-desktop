package app.youmu.daynote;

import android.app.Notification;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;

import java.time.Instant;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.Locale;

/** Delivers only an already scheduled local reminder; never starts network work. */
public final class ReminderReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context, Intent intent) {
        if (!ReminderScheduler.ACTION_REMIND.equals(intent.getAction())) return;
        String id = intent.getStringExtra("task_id");
        String expected = intent.getStringExtra(ReminderScheduler.EXTRA_FINGERPRINT);
        if (id == null || expected == null) return;
        SyncCore.Record current = null;
        for (SyncCore.Record record : CalendarStore.get(context).list()) {
            if (id.equals(record.id)) { current = record; break; }
        }
        if (current == null || current.data.completed || current.data.reminderMinutes < 0) {
            ReminderScheduler.reschedule(context); return;
        }
        String actual = ReminderPolicy.fingerprint(current);
        if (!expected.equals(actual) || ReminderScheduler.wasNotified(context, id, actual)) {
            ReminderScheduler.reschedule(context); return;
        }
        Instant trigger;
        try { trigger = current.data.reminderInstant(); }
        catch (RuntimeException invalid) { return; }
        if (trigger == null || trigger.isAfter(Instant.now().plusSeconds(1))) {
            ReminderScheduler.reschedule(context); return;
        }
        ReminderScheduler.ensureChannel(context);
        if (!ReminderScheduler.notificationsAllowed(context)) return;
        Intent open = new Intent(context, MainActivity.class)
                .setAction("app.youmu.daynote.OPEN_TASK")
                .setData(new Uri.Builder().scheme("daynote").authority("task").appendPath(id).build())
                .putExtra("task_id", id)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        PendingIntent content = PendingIntent.getActivity(context, 0, open,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        String when = "日程提醒";
        try {
            if (current.data.hasTime && current.data.dueInstant() != null) {
                when = DateTimeFormatter.ofPattern("M月d日 HH:mm", Locale.CHINA)
                        .withZone(ZoneId.systemDefault()).format(current.data.dueInstant());
            } else if (current.data.dueDate() != null) {
                when = current.data.dueDate().format(DateTimeFormatter.ofPattern("M月d日", Locale.CHINA)) + " · 全天";
            }
        } catch (RuntimeException ignored) { }
        Notification notification = new Notification.Builder(context, ReminderScheduler.CHANNEL_ID)
                .setSmallIcon(R.drawable.widget_notification_icon)
                .setContentTitle(current.data.title)
                .setContentText(when)
                .setContentIntent(content)
                .setCategory(Notification.CATEGORY_REMINDER)
                .setVisibility(Notification.VISIBILITY_SECRET)
                .setOnlyAlertOnce(true)
                .setAutoCancel(true)
                .setShowWhen(true)
                .setWhen(trigger.toEpochMilli())
                .build();
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        if (manager == null) return;
        try {
            manager.notify("task:" + id, 1, notification);
            ReminderScheduler.markNotified(context, id, actual);
        } catch (SecurityException ignored) { return; }
        ReminderScheduler.reschedule(context);
        AgendaWidgetProvider.refreshAll(context);
    }
}
