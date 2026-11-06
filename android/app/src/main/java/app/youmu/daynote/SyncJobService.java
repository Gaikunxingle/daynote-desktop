package app.youmu.daynote;

import android.app.job.JobInfo;
import android.app.job.JobParameters;
import android.app.job.JobScheduler;
import android.app.job.JobService;
import android.content.ComponentName;
import android.content.Context;

import java.util.HashMap;
import java.util.Map;

/** Network-constrained background sync. The OS may delay the 15 minute cadence. */
public final class SyncJobService extends JobService {
    private static final int PERIODIC_JOB = 250601;
    private static final int REFRESH_JOB = 250602;
    private static final long PERIOD_MILLIS = 15 * 60 * 1000L;
    private final Map<Integer, JobParameters> active = new HashMap<>();

    public static void ensureScheduled(Context context) {
        Context app = context.getApplicationContext();
        JobScheduler scheduler = app.getSystemService(JobScheduler.class);
        if (scheduler == null) return;
        if (!CalendarStore.get(app).connected()) {
            scheduler.cancel(PERIODIC_JOB); scheduler.cancel(REFRESH_JOB); return;
        }
        // Avoid resetting the periodic clock whenever foreground data changes.
        if (scheduler.getPendingJob(PERIODIC_JOB) == null) {
            JobInfo info = new JobInfo.Builder(PERIODIC_JOB, new ComponentName(app, SyncJobService.class))
                    .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                    .setPeriodic(PERIOD_MILLIS)
                    .setPersisted(true)
                    .setBackoffCriteria(60_000L, JobInfo.BACKOFF_POLICY_EXPONENTIAL)
                    .build();
            scheduler.schedule(info);
        }
    }

    public static void requestNow(Context context) {
        Context app = context.getApplicationContext();
        ensureScheduled(app);
        if (!CalendarStore.get(app).connected()) return;
        JobScheduler scheduler = app.getSystemService(JobScheduler.class);
        if (scheduler == null || scheduler.getPendingJob(REFRESH_JOB) != null) return;
        JobInfo info = new JobInfo.Builder(REFRESH_JOB, new ComponentName(app, SyncJobService.class))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                .setMinimumLatency(1L)
                .setBackoffCriteria(60_000L, JobInfo.BACKOFF_POLICY_EXPONENTIAL)
                .build();
        scheduler.schedule(info);
    }

    @Override public boolean onStartJob(JobParameters params) {
        CalendarStore store = CalendarStore.get(this);
        if (!store.connected()) return false;
        active.put(params.getJobId(), params);
        store.syncAsync(() -> {
            if (active.get(params.getJobId()) != params) return;
            active.remove(params.getJobId());
            AgendaWidgetProvider.refreshAll(this);
            ReminderScheduler.reschedule(this);
            String error = store.error();
            jobFinished(params, store.connected() && error != null && !error.trim().isEmpty());
        });
        return true;
    }

    @Override public boolean onStopJob(JobParameters params) {
        if (active.get(params.getJobId()) == params) active.remove(params.getJobId());
        // CalendarStore keeps in-flight mutations durable; a stopped job never
        // clears them. Retry only while this profile still has credentials.
        return CalendarStore.get(this).connected();
    }
}
