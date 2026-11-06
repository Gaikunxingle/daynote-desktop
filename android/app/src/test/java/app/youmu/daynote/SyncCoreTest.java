package app.youmu.daynote;

import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.Test;

import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.time.LocalDate;
import java.time.ZoneId;
import java.util.Collections;
import java.util.List;

import static org.junit.Assert.*;

public class SyncCoreTest {
    private static final String ACCOUNT_A="77777777-7777-4777-8777-777777777777";
    private static final String ACCOUNT_B="88888888-8888-4888-8888-888888888888";
    private SyncCore.TaskData task(String title) { SyncCore.TaskData t=new SyncCore.TaskData(); t.title=title; return t; }
    private SyncCore.State bound() { SyncCore.State s=new SyncCore.State(); s.accountId=ACCOUNT_A; return s; }
    private SyncCore.CloudRecord cloud(String id,long version,SyncCore.TaskData data,boolean deleted) {
        SyncCore.CloudRecord r=new SyncCore.CloudRecord(); r.id=id; r.version=version; r.data=data.copy(); r.deleted=deleted; return r;
    }
    private SyncCore.Response empty(String account) {
        SyncCore.Response r=new SyncCore.Response(); r.accountId=account; r.accountLabel="Fixture"; r.serverTime="2026-11-06T01:00:00Z"; return r;
    }
    private SyncCore.Response response(SyncCore.Mutation m,long version,String status) {
        SyncCore.Response r=empty(ACCOUNT_A); SyncCore.Result result=new SyncCore.Result(); result.id=m.id; result.mutationId=m.mutationId;
        result.status=status; result.version=version; r.results.add(result);
        if(version>0)r.items.add(cloud(m.id,version,m.data,m.deleted)); return r;
    }
    private SyncCore.State savedRemote(String title,long version) {
        SyncCore.State s=bound(); SyncCore.Record r=new SyncCore.Record(); r.data=task(title); r.version=version; r.dirty=false;
        s.records.put(r.id,r); return s;
    }
    private String onlyId(SyncCore.State state) { return state.records.keySet().iterator().next(); }

    @Test public void nullDatesAreExplicitOnWire() throws Exception {
        JSONObject json=task("未安排").toJson(); assertTrue(json.has("due_at")); assertTrue(json.isNull("due_at")); assertTrue(json.isNull("end_at"));
        assertEquals(task("未安排"),SyncCore.TaskData.fromJson(json,false));
    }
    @Test public void allDayReminderUsesLocalNineAM() {
        SyncCore.TaskData data=task("全天"); data.hasTime=false; data.dueAt="2026-11-06"; data.reminderMinutes=10;
        assertEquals(LocalDate.of(2026,11,6),data.dueDate());
        assertEquals(LocalDate.of(2026,11,6).atTime(8,50).atZone(ZoneId.systemDefault()).toInstant(),data.reminderInstant());
        data.completed=true; assertNull(data.reminderInstant());
    }
    @Test public void timedDateKeepsExplicitOffsetAndDeviceLocalDate() {
        SyncCore.TaskData data=task("讨论"); data.dueAt="2026-11-06T14:00:00+08:00"; data.reminderMinutes=60;
        assertEquals(Instant.parse("2026-11-06T06:00:00Z"),data.dueInstant());
        assertEquals(Instant.parse("2026-11-06T05:00:00Z"),data.reminderInstant());
        assertEquals(data.dueInstant().atZone(ZoneId.systemDefault()).toLocalDate(),data.dueDate());
    }
    @Test public void requestSurvivesPersistenceAndSubsequentOfflineEditing() throws Exception {
        SyncCore.State state=bound(); state.edit(null,task("第一次"),-1); List<SyncCore.Mutation> first=state.prepareBatch();
        SyncCore.State restored=SyncCore.State.fromJson(new JSONObject(state.toJson().toString()));
        restored.edit(first.get(0).id,task("后来编辑"),-1);
        List<SyncCore.Mutation> retry=restored.prepareBatch();
        assertEquals(first.get(0).mutationId,retry.get(0).mutationId); assertEquals(first.get(0).data,retry.get(0).data);
        assertEquals("后来编辑",restored.records.get(first.get(0).id).data.title);
    }
    @Test public void editDuringRequestRebasesOntoAcceptedVersion() throws Exception {
        SyncCore.State state=bound(); state.edit(null,task("初稿"),-1); List<SyncCore.Mutation> sent=state.prepareBatch(); String id=sent.get(0).id;
        state.edit(id,task("发送中继续编辑"),-1);
        SyncCore.State merged=SyncCore.merge(state,response(sent.get(0),1,"applied"),sent).state;
        assertEquals("发送中继续编辑",merged.records.get(id).data.title); assertEquals(1,merged.records.get(id).version);
        assertTrue(merged.records.get(id).dirty); assertTrue(merged.pending.isEmpty());
        SyncCore.Mutation next=merged.prepareBatch().get(0); assertEquals(1,next.baseVersion); assertNotEquals(sent.get(0).mutationId,next.mutationId);
    }
    @Test public void newerRemoteAndNewLocalAfterLostReplyBothSurvive() throws Exception {
        SyncCore.State state=bound(); state.edit(null,task("初稿"),-1); List<SyncCore.Mutation> sent=state.prepareBatch(); String id=sent.get(0).id;
        state.edit(id,task("本机后来修改"),-1);
        SyncCore.Response result=response(sent.get(0),1,"applied"); result.items.set(0,cloud(id,2,task("另一设备后来修改"),false));
        SyncCore.Merge merged=SyncCore.merge(state,result,sent);
        assertEquals(1,merged.conflicts); assertEquals(2,merged.state.visible().size());
        assertEquals("另一设备后来修改",merged.state.records.get(id).data.title);
        assertTrue(merged.state.visible().stream().anyMatch(r->r.data.title.equals("本机后来修改（冲突副本）")));
    }
    @Test public void lostReplyWithNoFurtherLocalEditUsesCurrentRemote() throws Exception {
        SyncCore.State state=bound(); state.edit(null,task("本机"),-1); List<SyncCore.Mutation> sent=state.prepareBatch(); String id=sent.get(0).id;
        SyncCore.Response result=response(sent.get(0),1,"applied"); result.items.set(0,cloud(id,2,task("另一设备"),false));
        SyncCore.Merge merged=SyncCore.merge(state,result,sent);
        assertEquals(0,merged.conflicts); assertEquals("另一设备",merged.state.records.get(id).data.title); assertEquals(0,merged.state.dirtyCount());
    }
    @Test public void conflictPreservesLocalCopyAndRemoteOriginal() throws Exception {
        SyncCore.State state=savedRemote("原日程",1); String id=onlyId(state); state.edit(id,task("本机修改"),-1);
        List<SyncCore.Mutation> sent=state.prepareBatch(); SyncCore.Response result=response(sent.get(0),2,"conflict"); result.items.get(0).data=task("云端修改");
        SyncCore.Merge merged=SyncCore.merge(state,result,sent);
        assertEquals(1,merged.conflicts); assertEquals("云端修改",merged.state.records.get(id).data.title);
        SyncCore.Record copy=merged.state.visible().stream().filter(r->!r.id.equals(id)).findFirst().orElseThrow();
        assertEquals("本机修改（冲突副本）",copy.data.title); assertEquals(0,copy.version); assertTrue(copy.dirty);
    }
    @Test public void deletionPropagatesAsTombstoneAndSurvivesPersistence() throws Exception {
        SyncCore.State state=savedRemote("删除我",1); String id=onlyId(state); state.delete(id); List<SyncCore.Mutation> sent=state.prepareBatch();
        SyncCore.State merged=SyncCore.merge(state,response(sent.get(0),2,"applied"),sent).state;
        assertEquals(0,merged.visible().size()); assertTrue(merged.records.get(id).deleted);
        SyncCore.State restored=SyncCore.State.fromJson(new JSONObject(merged.toJson().toString()));
        assertEquals(0,restored.visible().size()); assertEquals(2,restored.records.get(id).version); assertFalse(restored.records.get(id).dirty);
    }
    @Test public void emptyCloudTombstoneIsPersistable() throws Exception {
        String id=SyncCore.newId(); SyncCore.Response response=empty(ACCOUNT_A); response.items.add(cloud(id,1,new SyncCore.TaskData(),true));
        SyncCore.State merged=SyncCore.merge(bound(),response,Collections.emptyList()).state;
        SyncCore.State restored=SyncCore.State.fromJson(new JSONObject(merged.toJson().toString()));
        assertTrue(restored.records.get(id).deleted); assertTrue(restored.visible().isEmpty());
    }
    @Test public void deleteConflictKeepsNewerCloudEventWithoutCopy() throws Exception {
        SyncCore.State state=savedRemote("旧日程",1); String id=onlyId(state); state.delete(id); List<SyncCore.Mutation> sent=state.prepareBatch();
        SyncCore.Response result=response(sent.get(0),2,"conflict"); result.items.set(0,cloud(id,2,task("已改期"),false));
        SyncCore.Merge merged=SyncCore.merge(state,result,sent);
        assertEquals(1,merged.conflicts); assertEquals(1,merged.state.visible().size()); assertEquals("已改期",merged.state.records.get(id).data.title);
    }
    @Test public void deleteWhileCreateInFlightCreatesSuccessorDeletion() throws Exception {
        SyncCore.State state=bound(); state.edit(null,task("新建后取消"),-1); List<SyncCore.Mutation> sent=state.prepareBatch(); String id=sent.get(0).id;
        state.delete(id); SyncCore.State merged=SyncCore.merge(state,response(sent.get(0),1,"applied"),sent).state;
        assertTrue(merged.visible().isEmpty()); SyncCore.Mutation next=merged.prepareBatch().get(0);
        assertTrue(next.deleted); assertEquals(1,next.baseVersion); assertNotEquals(sent.get(0).mutationId,next.mutationId);
    }
    @Test public void wrongAccountRejectedWithoutTouchingSource() throws Exception {
        SyncCore.State state=bound(); state.edit(null,task("私密"),-1); String before=state.toJson().toString();
        try { SyncCore.merge(state,empty(ACCOUNT_B),Collections.emptyList()); fail("wrong account accepted"); }
        catch(IllegalArgumentException expected) { assertEquals(before,state.toJson().toString()); }
    }
    @Test public void emptyFirstHandshakeRetainsUnboundOfflineCalendar() {
        SyncCore.State state=new SyncCore.State(); state.edit(null,task("离线日程"),-1);
        SyncCore.State merged=SyncCore.merge(state,empty(ACCOUNT_A),Collections.emptyList()).state;
        assertEquals(ACCOUNT_A,merged.accountId); assertEquals(1,merged.visible().size()); assertEquals(1,merged.dirtyCount());
    }
    @Test public void truncatedOrRegressedSnapshotRejectedWithoutChanges() throws Exception {
        SyncCore.State state=savedRemote("已同步",3); String before=state.toJson().toString();
        for(int version:new int[]{0,2}) {
            SyncCore.Response response=empty(ACCOUNT_A); if(version>0)response.items.add(cloud(onlyId(state),version,task("旧版"),false));
            try { SyncCore.merge(state,response,Collections.emptyList()); fail("bad snapshot accepted"); }
            catch(IllegalArgumentException expected) { assertEquals(before,state.toJson().toString()); }
        }
    }
    @Test public void incompleteAcknowledgementsCannotDropPending() throws Exception {
        SyncCore.State state=bound(); state.edit(null,task("等待确认"),-1); List<SyncCore.Mutation> sent=state.prepareBatch(); String before=state.toJson().toString();
        try { SyncCore.merge(state,empty(ACCOUNT_A),sent); fail("incomplete ack accepted"); }
        catch(IllegalArgumentException expected) { assertEquals(before,state.toJson().toString()); assertEquals(1,state.pending.size()); }
    }
    @Test public void editorRevisionDetectsConcurrentChanges() {
        SyncCore.State state=savedRemote("原内容",1); String id=onlyId(state); long revision=state.records.get(id).revision;
        state.edit(id,task("新内容"),-1); assertTrue(state.edit(id,task("编辑器草稿"),revision));
        assertEquals("新内容",state.records.get(id).data.title); assertEquals(2,state.visible().size());
    }
    @Test public void byteBudgetIncludesLongChineseNotes() throws Exception {
        SyncCore.State state=bound();
        for(int i=0;i<100;i++) { SyncCore.TaskData d=task("事项"+i); d.notes="测".repeat(20000); state.edit(null,d,-1); }
        List<SyncCore.Mutation> batch=state.prepareBatch(); JSONArray json=new JSONArray(); for(SyncCore.Mutation mutation:batch)json.put(mutation.toJson());
        assertFalse(batch.isEmpty()); assertTrue(batch.size()<100); assertTrue(json.toString().getBytes(StandardCharsets.UTF_8).length<850000);
        assertEquals(100,state.pending.size());
    }
    @Test public void badDatesAndJsonTypesAreRejected() throws Exception {
        for(String date:new String[]{"2026-02-31T12:00:00Z","2026-11-06T12:00:00","/Date(123456)/"}) {
            SyncCore.TaskData data=task("坏日期"); data.dueAt=date;
            try { data.validate(); fail("invalid date accepted"); } catch(IllegalArgumentException expected) { }
        }
        JSONObject bad=task("类型").toJson().put("completed","false");
        try { SyncCore.TaskData.fromJson(bad,false); fail("string boolean accepted"); } catch(org.json.JSONException expected) { }
    }
    @Test public void copiesDoNotSplitUnicodeCodePointsOrExceedCloudLimit() {
        String title="😀".repeat(200); String copy=SyncCore.copyTitle(title,"（冲突副本）");
        assertEquals(200,copy.codePointCount(0,copy.length())); SyncCore.TaskData data=task(copy); data.validate();
        data.title=title+"x"; try { data.validate(); fail("too many scalars accepted"); } catch(IllegalArgumentException expected) { }
    }
    @Test public void malformedPendingStateFailsClosed() throws Exception {
        SyncCore.State state=bound(); state.edit(null,task("事项"),-1); state.prepareBatch(); JSONObject json=state.toJson();
        JSONArray pending=json.getJSONArray("pending"); pending.put(pending.get(0));
        try { SyncCore.State.fromJson(json); fail("duplicate pending accepted"); } catch(IllegalArgumentException expected) { }
    }
}
