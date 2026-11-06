package app.youmu.daynote;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** Normal boot only: credentials and calendar are not read before device unlock. */
public final class BootReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context, Intent intent) {
        String action = intent.getAction();
        if (!Intent.ACTION_BOOT_COMPLETED.equals(action)
                && !Intent.ACTION_MY_PACKAGE_REPLACED.equals(action)
                && !Intent.ACTION_TIME_CHANGED.equals(action)
                && !Intent.ACTION_TIMEZONE_CHANGED.equals(action)) return;
        ReminderScheduler.reschedule(context);
        AgendaWidgetProvider.refreshAll(context);
        SyncJobService.ensureScheduled(context);
        if (Intent.ACTION_BOOT_COMPLETED.equals(action) || Intent.ACTION_MY_PACKAGE_REPLACED.equals(action))
            SyncJobService.requestNow(context);
    }
}
