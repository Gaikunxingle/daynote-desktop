package app.youmu.daynote;

import android.Manifest;
import android.app.AlarmManager;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/** Inexact, device-local reminders. No schedule text or credential is stored here. */
public final class ReminderScheduler {
    public static final String CHANNEL_ID = "daynote_reminders";
    static final String ACTION_REMIND = "app.youmu.daynote.REMIND";
    static final String EXTRA_FINGERPRINT = "reminder_fingerprint";
    static final String PREFS = "daynote_local_reminders";
    private static final String SCHEDULED_IDS = "scheduled_ids";
    private ReminderScheduler() {}

    public static void ensureChannel(Context context) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        if (manager == null) return;
        NotificationChannel channel = new NotificationChannel(CHANNEL_ID, "日程提醒", NotificationManager.IMPORTANCE_DEFAULT);
        channel.setDescription("根据本机日程发送普通提醒；省电和系统调度可能延迟。");
        channel.setLockscreenVisibility(Notification.VISIBILITY_SECRET);
        channel.enableVibration(true);
        manager.createNotificationChannel(channel);
    }

    public static boolean notificationsAllowed(Context context) {
        NotificationManager manager = context.getSystemService(NotificationManager.class);
        if (manager == null || !manager.areNotificationsEnabled()) return false;
        if (Build.VERSION.SDK_INT >= 33 &&
                context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return false;
        NotificationChannel channel = manager.getNotificationChannel(CHANNEL_ID);
        return channel == null || channel.getImportance() != NotificationManager.IMPORTANCE_NONE;
    }

    public static void reschedule(Context context) {
        Context app = context.getApplicationContext();
        ensureChannel(app);
        AlarmManager alarms = app.getSystemService(AlarmManager.class);
        if (alarms == null) return;
        SharedPreferences prefs = app.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        Set<String> oldIds = new HashSet<>(prefs.getStringSet(SCHEDULED_IDS, Collections.emptySet()));
        List<SyncCore.Record> records = CalendarStore.get(app).list();
        Map<String, String> notified = new HashMap<>();
        Map<String, String> scheduled = new HashMap<>();
        for (SyncCore.Record record : records) {
            notified.put(record.id, prefs.getString("notified." + record.id, ""));
            scheduled.put(record.id, prefs.getString("scheduled." + record.id, ""));
        }
        List<ReminderPolicy.Plan> plans = notificationsAllowed(app)
                ? ReminderPolicy.plan(records, notified, scheduled, Instant.now()) : Collections.emptyList();
        Set<String> nextIds = new HashSet<>();
        SharedPreferences.Editor edit = prefs.edit();
        for (ReminderPolicy.Plan plan : plans) {
            try {
                PendingIntent pending = pending(app, plan.record.id, plan.fingerprint, PendingIntent.FLAG_UPDATE_CURRENT);
                alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, plan.deliveryMillis, pending);
                nextIds.add(plan.record.id);
                edit.putString("scheduled." + plan.record.id, plan.fingerprint);
            } catch (RuntimeException ignored) {
                // The next foreground/sync/boot refresh retries; never log schedule data.
            }
        }
        NotificationManager notifications = app.getSystemService(NotificationManager.class);
        Set<String> visibleIds = new HashSet<>();
        for (SyncCore.Record record : records) {
            visibleIds.add(record.id);
            if (record.data.completed && notifications != null) notifications.cancel("task:" + record.id, 1);
        }
        for (String id : oldIds) {
            if (!nextIds.contains(id)) {
                PendingIntent pending = pending(app, id, "", PendingIntent.FLAG_NO_CREATE);
                if (pending != null) { alarms.cancel(pending); pending.cancel(); }
                edit.remove("scheduled." + id);
            }
            if (!visibleIds.contains(id) && notifications != null) notifications.cancel("task:" + id, 1);
        }
        if (notifications != null) {
            Map<String, SyncCore.Record> visible = new HashMap<>();
            for (SyncCore.Record record : records) visible.put(record.id, record);
            for (android.service.notification.StatusBarNotification shown : notifications.getActiveNotifications()) {
                String tag = shown.getTag();
                if (tag == null || !tag.startsWith("task:")) continue;
                String id = tag.substring(5);
                SyncCore.Record record = visible.get(id);
                if (record == null || record.data.completed ||
                        !ReminderPolicy.fingerprint(record).equals(prefs.getString("notified." + id, ""))) {
                    notifications.cancel(tag, shown.getId());
                }
            }
        }
        // Retain notification fingerprints after completion/deletion so a stale
        // download or restore cannot immediately replay a previously sent reminder.
        edit.putStringSet(SCHEDULED_IDS, nextIds).apply();
    }

    static PendingIntent pending(Context context, String taskId, String fingerprint, int flags) {
        Intent intent = new Intent(context, ReminderReceiver.class)
                .setAction(ACTION_REMIND)
                .setData(new Uri.Builder().scheme("daynote").authority("reminder").appendPath(taskId).build())
                .putExtra("task_id", taskId)
                .putExtra(EXTRA_FINGERPRINT, fingerprint);
        return PendingIntent.getBroadcast(context, 0, intent, flags | PendingIntent.FLAG_IMMUTABLE);
    }

    static boolean wasNotified(Context context, String id, String fingerprint) {
        return fingerprint.equals(context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString("notified." + id, ""));
    }

    static void markNotified(Context context, String id, String fingerprint) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
                .putString("notified." + id, fingerprint).commit();
    }
}

/** Pure planning logic, deliberately independent of Android services. */
final class ReminderPolicy {
    static final int MAX_ALARMS = 60;
    static final long RECENT_NEW_MILLIS = 2 * 60 * 1000L;
    static final long REBOOT_CATCHUP_MILLIS = 24 * 60 * 60 * 1000L;
    static final class Plan {
        final SyncCore.Record record;
        final String fingerprint;
        final long deliveryMillis;
        Plan(SyncCore.Record record, String fingerprint, long deliveryMillis) {
            this.record = record; this.fingerprint = fingerprint; this.deliveryMillis = deliveryMillis;
        }
    }
    private ReminderPolicy() {}

    static String fingerprint(SyncCore.Record record) {
        SyncCore.TaskData data = record.data;
        String date = data.dueAt == null ? "" : data.hasTime ? data.dueInstant().toString() : data.dueDate().toString();
        String value = record.id + "\n" + date + "\n" + data.hasTime + "\n"
                + data.reminderMinutes + "\n" + data.isDeadline;
        try {
            byte[] hash = MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8));
            StringBuilder result = new StringBuilder(64);
            for (byte b : hash) result.append(String.format(java.util.Locale.ROOT, "%02x", b & 255));
            return result.toString();
        } catch (java.security.NoSuchAlgorithmException impossible) { throw new IllegalStateException(impossible); }
    }

    static List<Plan> plan(List<SyncCore.Record> records, Map<String, String> notified,
                           Map<String, String> scheduled, Instant now) {
        List<Plan> result = new ArrayList<>();
        long nowMillis = now.toEpochMilli();
        for (SyncCore.Record record : records) {
            if (record == null || record.deleted || record.data == null || record.data.completed || record.data.reminderMinutes < 0) continue;
            try {
                Instant trigger = record.data.reminderInstant();
                if (trigger == null) continue;
                String fingerprint = fingerprint(record);
                if (fingerprint.equals(notified.get(record.id))) continue;
                long millis = trigger.toEpochMilli();
                long grace = fingerprint.equals(scheduled.get(record.id)) ? REBOOT_CATCHUP_MILLIS : RECENT_NEW_MILLIS;
                if (millis < nowMillis - grace) continue;
                result.add(new Plan(record, fingerprint, Math.max(nowMillis + 1000L, millis)));
            } catch (RuntimeException ignored) {
                // Invalid dates are never converted into an immediate reminder.
            }
        }
        result.sort(Comparator.comparingLong((Plan p) -> p.deliveryMillis).thenComparing(p -> p.record.id));
        if (result.size() > MAX_ALARMS) return new ArrayList<>(result.subList(0, MAX_ALARMS));
        return result;
    }
}
