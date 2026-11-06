package app.youmu.daynote;

import org.junit.Test;

import java.time.Instant;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.TimeZone;

import static org.junit.Assert.*;

public class ReminderPolicyTest {
    private static final Instant NOW = Instant.parse("2026-11-06T00:00:00Z");
    private static SyncCore.Record timed(String at) {
        SyncCore.Record record = new SyncCore.Record();
        record.data.title = "Fictional reminder fixture";
        record.data.dueAt = at;
        record.data.hasTime = true;
        record.data.reminderMinutes = 0;
        return record;
    }

    @Test public void unrelatedEditsAndEquivalentOffsetsDoNotRearm() {
        SyncCore.Record record = timed("2026-11-06T09:00:00+08:00");
        String before = ReminderPolicy.fingerprint(record);
        record.data.title = "Renamed"; record.data.notes = "Changed notes";
        record.data.colorId = "pink"; record.data.endAt = "2026-11-06T02:00:00Z"; record.revision++;
        record.data.dueAt = "2026-11-06T01:00:00.000Z";
        assertEquals(before, ReminderPolicy.fingerprint(record));
        Map<String,String> notified = Collections.singletonMap(record.id, before);
        assertTrue(ReminderPolicy.plan(Collections.singletonList(record), notified, Collections.emptyMap(), NOW).isEmpty());
    }

    @Test public void changedTimingGetsNewFingerprintAndPlan() {
        SyncCore.Record record = timed("2026-11-06T01:00:00Z");
        String before = ReminderPolicy.fingerprint(record);
        record.data.dueAt = "2026-11-06T03:00:00Z";
        List<ReminderPolicy.Plan> plan = ReminderPolicy.plan(Collections.singletonList(record),
                Collections.singletonMap(record.id, before), Collections.emptyMap(), NOW);
        assertEquals(1, plan.size());
        assertNotEquals(before, plan.get(0).fingerprint);
        assertEquals(Instant.parse("2026-11-06T03:00:00Z").toEpochMilli(), plan.get(0).deliveryMillis);
    }

    @Test public void completedDeletedDisabledAndUndatedAreExcluded() {
        List<SyncCore.Record> records = new ArrayList<>();
        SyncCore.Record complete = timed("2026-11-06T01:00:00Z"); complete.data.completed = true; records.add(complete);
        SyncCore.Record deleted = timed("2026-11-06T01:00:00Z"); deleted.deleted = true; records.add(deleted);
        SyncCore.Record disabled = timed("2026-11-06T01:00:00Z"); disabled.data.reminderMinutes = -1; records.add(disabled);
        SyncCore.Record undated = timed(null); records.add(undated);
        assertTrue(ReminderPolicy.plan(records, Collections.emptyMap(), Collections.emptyMap(), NOW).isEmpty());
    }

    @Test public void allDayUsesLocalNineAndLeadTime() {
        TimeZone original = TimeZone.getDefault();
        try {
            TimeZone.setDefault(TimeZone.getTimeZone("Asia/Shanghai"));
            SyncCore.Record record = timed("2026-11-06");
            record.data.hasTime = false; record.data.reminderMinutes = 15;
            List<ReminderPolicy.Plan> plan = ReminderPolicy.plan(Collections.singletonList(record),
                    Collections.emptyMap(), Collections.emptyMap(), NOW);
            assertEquals(1, plan.size());
            assertEquals(Instant.parse("2026-11-06T00:45:00Z").toEpochMilli(), plan.get(0).deliveryMillis);
        } finally { TimeZone.setDefault(original); }
    }

    @Test public void newHistoricalDownloadsDoNotCreateNotificationFlood() {
        SyncCore.Record record = timed("2026-11-05T20:00:00Z");
        assertTrue(ReminderPolicy.plan(Collections.singletonList(record),
                Collections.emptyMap(), Collections.emptyMap(), NOW).isEmpty());
    }

    @Test public void previouslyScheduledReminderCanCatchUpAfterRestart() {
        SyncCore.Record record = timed("2026-11-05T20:00:00Z");
        Map<String,String> scheduled = Collections.singletonMap(record.id, ReminderPolicy.fingerprint(record));
        List<ReminderPolicy.Plan> plan = ReminderPolicy.plan(Collections.singletonList(record),
                Collections.emptyMap(), scheduled, NOW);
        assertEquals(1, plan.size());
        assertEquals(NOW.toEpochMilli() + 1000L, plan.get(0).deliveryMillis);
        assertTrue(ReminderPolicy.plan(Collections.singletonList(record), Collections.emptyMap(),
                scheduled, NOW.plusSeconds(24 * 60 * 60)).isEmpty());
    }

    @Test public void nearestSixtyAlarmsAreChosenWithoutChangingRecords() {
        List<SyncCore.Record> records = new ArrayList<>();
        for (int i = 90; i >= 1; i--) records.add(timed(NOW.plusSeconds(i * 60L).toString()));
        List<ReminderPolicy.Plan> plan = ReminderPolicy.plan(records, Collections.emptyMap(), Collections.emptyMap(), NOW);
        assertEquals(60, plan.size());
        assertEquals(NOW.plusSeconds(60).toEpochMilli(), plan.get(0).deliveryMillis);
        assertEquals(NOW.plusSeconds(3600).toEpochMilli(), plan.get(59).deliveryMillis);
        assertEquals(90, records.size());
        assertEquals(NOW.plusSeconds(5400).toString(), records.get(0).data.dueAt);
    }

    @Test public void invalidDatesNeverBecomeImmediateAlarms() {
        SyncCore.Record invalid = timed("not-a-date");
        assertTrue(ReminderPolicy.plan(Collections.singletonList(invalid), new HashMap<>(), new HashMap<>(), NOW).isEmpty());
    }
}
