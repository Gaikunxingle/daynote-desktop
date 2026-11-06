package app.youmu.daynote;

import android.app.job.JobInfo;
import android.app.job.JobParameters;
import android.app.job.JobScheduler;
import android.app.job.JobService;
import android.content.ComponentName;
import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;

import java.util.HashMap;
import java.util.Map;

/** Network-constrained background sync. The OS may delay the 15 minute cadence. */
public final class SyncJobService extends JobService {
    private static final int PERIODIC_JOB = 250601;
    private static final int REFRESH_JOB = 250602;
    private static final long PERIOD_MILLIS = 15 * 60 * 1000L;
    private static final int MAX_PASSES = 4;
    private static final long MAX_RUN_MILLIS = 90_000L;
    private final Handler main = new Handler(Looper.getMainLooper());
    private final Map<Integer, Run> active = new HashMap<>();

    private static final class Run {
        final JobParameters parameters;
        final long started = SystemClock.elapsedRealtime();
        int passes;
        Run(JobParameters parameters) { this.parameters = parameters; }
    }

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
        if (!store.connected() || !store.canWrite()) return false;
        Run run = new Run(params);
        active.put(params.getJobId(), run);
        // Return the ongoing-work result before any synchronous store failure
        // can invoke its callback and finish this job.
        main.post(() -> syncPass(store, run));
        return true;
    }

    private void syncPass(CalendarStore store, Run run) {
        if (active.get(run.parameters.getJobId()) != run) return;
        // A continuation is posted after the previous callback; state can change
        // before it runs, so recheck both connection and the bounded work budget.
        if (!store.connected() || !store.canWrite()) { finish(run, false); return; }
        if (run.passes >= MAX_PASSES || SystemClock.elapsedRealtime() - run.started >= MAX_RUN_MILLIS) {
            finish(run, store.pendingCount() > 0); return;
        }
        run.passes++;
        store.syncAsync(() -> {
            if (active.get(run.parameters.getJobId()) != run) return;
            AgendaWidgetProvider.refreshAll(this);
            ReminderScheduler.reschedule(this);
            String error = store.error();
            boolean failed = error != null && !error.trim().isEmpty();
            boolean usable = store.connected() && store.canWrite();
            boolean pending = store.pendingCount() > 0;
            // A widget can save while another request is in flight. syncAsync's
            // callback then belongs to that older request, which may not contain
            // the widget edit. The background store does not schedule a next
            // pass itself, so keep this job alive to send the remaining changes.
            if (!failed && usable && pending && run.passes < MAX_PASSES
                    && SystemClock.elapsedRealtime() - run.started < MAX_RUN_MILLIS) {
                main.post(() -> syncPass(store, run));
            } else {
                // Failed/offline requests never loop immediately. JobScheduler
                // uses the existing exponential backoff for failure or overflow.
                finish(run, usable && (failed || pending));
            }
        });
    }

    private void finish(Run run, boolean retry) {
        if (active.get(run.parameters.getJobId()) != run) return;
        active.remove(run.parameters.getJobId());
        jobFinished(run.parameters, retry);
    }

    @Override public boolean onStopJob(JobParameters params) {
        Run run = active.get(params.getJobId());
        if (run != null && run.parameters == params) active.remove(params.getJobId());
        // CalendarStore keeps in-flight mutations durable; a stopped job never
        // clears them. Retry only while this profile still has credentials.
        CalendarStore store = CalendarStore.get(this);
        return store.connected() && store.canWrite();
    }
}
