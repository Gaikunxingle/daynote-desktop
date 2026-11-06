package app.youmu.daynote;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.nio.charset.StandardCharsets;
import java.time.Instant;
import java.time.LocalDate;
import java.time.OffsetDateTime;
import java.time.ZoneId;
import java.time.format.DateTimeParseException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Set;
import java.util.UUID;

/** Pure Java sync model. No Android, network, or credential dependencies. */
public final class SyncCore {
    private SyncCore() {}
    public static String newId() { return UUID.randomUUID().toString().replace("-", ""); }
    private static boolean validId(String value) { return value != null && value.matches("[0-9a-f]{32}"); }
    private static void require(boolean condition, String message) {
        if (!condition) throw new IllegalArgumentException(message);
    }
    private static void validateAccount(String account) {
        if (account == null) return;
        try { require(UUID.fromString(account).toString().equalsIgnoreCase(account), "同步账号格式无效。"); }
        catch (RuntimeException error) { throw new IllegalArgumentException("同步账号格式无效。"); }
    }
    private static String nullableString(JSONObject json, String key) throws JSONException {
        if (!json.has(key)) throw new JSONException("Missing field " + key);
        return json.isNull(key) ? null : string(json, key);
    }
    private static String string(JSONObject json, String key) throws JSONException {
        Object value = json.get(key);
        if (!(value instanceof String)) throw new JSONException("Invalid text field " + key);
        return (String) value;
    }
    private static boolean bool(JSONObject json, String key) throws JSONException {
        Object value = json.get(key);
        if (!(value instanceof Boolean)) throw new JSONException("Invalid boolean field " + key);
        return (Boolean) value;
    }
    private static long integer(JSONObject json, String key) throws JSONException {
        Object value = json.get(key);
        if (!(value instanceof Number) || value instanceof Double || value instanceof Float)
            throw new JSONException("Invalid integer field " + key);
        return ((Number) value).longValue();
    }
    private static void putNullable(JSONObject json, String key, String value) throws JSONException {
        json.put(key, value == null ? JSONObject.NULL : value);
    }
    public static String copyTitle(String title, String suffix) {
        int allowed = 200 - suffix.codePointCount(0, suffix.length());
        int count = title.codePointCount(0, title.length());
        return title.substring(0, title.offsetByCodePoints(0, Math.min(allowed, count))) + suffix;
    }

    public static final class TaskData {
        public String title = "";
        public String notes = "";
        public String dueAt;
        public boolean hasTime = true;
        public int reminderMinutes = -1;
        public boolean completed;
        public String endAt;
        public String colorId = "blue";
        public boolean isDeadline;

        public TaskData copy() {
            TaskData d = new TaskData(); d.title=title; d.notes=notes; d.dueAt=dueAt; d.hasTime=hasTime;
            d.reminderMinutes=reminderMinutes; d.completed=completed; d.endAt=endAt;
            d.colorId=colorId; d.isDeadline=isDeadline; return d;
        }
        public void validate() {
            require(title != null && !title.trim().isEmpty() && title.codePointCount(0,title.length()) <= 200,
                    "请填写标题，最多 200 个字符。");
            require(notes != null && notes.codePointCount(0,notes.length()) <= 20000, "备注最多 20000 个字符。");
            require(reminderMinutes >= -1 && reminderMinutes <= 525600, "提醒时间超出范围。");
            require(Arrays.asList("blue","teal","purple","orange","green","red","pink","gray").contains(colorId), "日程颜色无效。");
            require(dueAt != null || endAt == null, "结束时间需要对应的开始时间。");
            try {
                if (hasTime) {
                    Instant start = parseInstant(dueAt), end = parseInstant(endAt);
                    require(end == null || !end.isBefore(start), "结束时间不能早于开始时间。");
                } else {
                    LocalDate start = parseDay(dueAt), end = parseDay(endAt);
                    require(end == null || !end.isBefore(start), "结束日期不能早于开始日期。");
                }
            } catch (DateTimeParseException error) { throw new IllegalArgumentException("日程日期或时区格式无效。"); }
        }
        private static Instant parseInstant(String value) {
            if (value == null) return null;
            require(value.matches("[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})"), "日程时刻需要明确时区。");
            return OffsetDateTime.parse(value).toInstant();
        }
        private static LocalDate parseDay(String value) {
            if (value == null) return null;
            require(value.matches("[0-9]{4}-[0-9]{2}-[0-9]{2}"), "全天日程日期格式无效。");
            return LocalDate.parse(value);
        }
        public LocalDate dueDate() {
            if (dueAt == null) return null;
            return hasTime ? parseInstant(dueAt).atZone(ZoneId.systemDefault()).toLocalDate() : parseDay(dueAt);
        }
        public Instant dueInstant() {
            if (dueAt == null) return null;
            return hasTime ? parseInstant(dueAt) : parseDay(dueAt).atStartOfDay(ZoneId.systemDefault()).toInstant();
        }
        public Instant reminderInstant() {
            if (completed || reminderMinutes < 0 || dueAt == null) return null;
            Instant base = hasTime ? dueInstant() : dueDate().atTime(9,0).atZone(ZoneId.systemDefault()).toInstant();
            return base.minusSeconds((long) reminderMinutes * 60);
        }
        public JSONObject toJson() throws JSONException {
            JSONObject j = new JSONObject(); j.put("title",title); j.put("notes",notes);
            putNullable(j,"due_at",dueAt); j.put("has_time",hasTime); j.put("reminder_minutes",reminderMinutes);
            j.put("completed",completed); putNullable(j,"end_at",endAt); j.put("color_id",colorId); j.put("is_deadline",isDeadline);
            return j;
        }
        public static TaskData fromJson(JSONObject j, boolean tombstone) throws JSONException {
            TaskData d = new TaskData();
            if (tombstone && j.length()==0) return d;
            d.title=string(j,"title"); d.notes=string(j,"notes"); d.dueAt=nullableString(j,"due_at");
            d.hasTime=bool(j,"has_time"); long reminder=integer(j,"reminder_minutes");
            require(reminder>=-1 && reminder<=525600,"提醒时间超出范围。"); d.reminderMinutes=(int)reminder;
            d.completed=bool(j,"completed"); d.endAt=nullableString(j,"end_at");
            d.colorId=string(j,"color_id"); d.isDeadline=bool(j,"is_deadline"); d.validate(); return d;
        }
        @Override public boolean equals(Object other) {
            if (!(other instanceof TaskData)) return false;
            TaskData d=(TaskData)other;
            return Objects.equals(title,d.title) && Objects.equals(notes,d.notes) && Objects.equals(dueAt,d.dueAt)
                    && hasTime==d.hasTime && reminderMinutes==d.reminderMinutes && completed==d.completed
                    && Objects.equals(endAt,d.endAt) && Objects.equals(colorId,d.colorId) && isDeadline==d.isDeadline;
        }
        @Override public int hashCode() { return Objects.hash(title,notes,dueAt,hasTime,reminderMinutes,completed,endAt,colorId,isDeadline); }
    }

    public static final class Record {
        public String id = newId();
        public TaskData data = new TaskData();
        public boolean deleted;
        public long version;
        public long revision = 1;
        public boolean dirty = true;
        public Record copy() {
            Record r=new Record(); r.id=id; r.data=data.copy(); r.deleted=deleted; r.version=version; r.revision=revision; r.dirty=dirty; return r;
        }
        JSONObject toJson() throws JSONException {
            return new JSONObject().put("id",id).put("data",deleted && data.title.isEmpty() ? new JSONObject() : data.toJson()).put("deleted",deleted)
                    .put("version",version).put("revision",revision).put("dirty",dirty);
        }
        static Record fromJson(JSONObject j) throws JSONException {
            Record r=new Record(); r.id=string(j,"id"); r.deleted=bool(j,"deleted"); r.version=integer(j,"version");
            r.revision=integer(j,"revision"); r.dirty=bool(j,"dirty"); r.data=TaskData.fromJson(j.getJSONObject("data"),r.deleted);
            require(validId(r.id)&&r.version>=0&&r.revision>=0,"本机事项结构无效。"); return r;
        }
    }
    public static final class Mutation {
        public String id;
        public String mutationId;
        public long baseVersion;
        public boolean deleted;
        public TaskData data;
        public Mutation copy() {
            Mutation m=new Mutation(); m.id=id; m.mutationId=mutationId; m.baseVersion=baseVersion; m.deleted=deleted; m.data=data.copy(); return m;
        }
        public JSONObject toJson() throws JSONException {
            return new JSONObject().put("id",id).put("mutation_id",mutationId).put("base_version",baseVersion)
                    .put("deleted",deleted).put("data",deleted && data.title.isEmpty() ? new JSONObject() : data.toJson());
        }
        static Mutation fromJson(JSONObject j) throws JSONException {
            Mutation m=new Mutation(); m.id=string(j,"id"); m.mutationId=string(j,"mutation_id");
            m.baseVersion=integer(j,"base_version"); m.deleted=bool(j,"deleted"); m.data=TaskData.fromJson(j.getJSONObject("data"),m.deleted);
            require(validId(m.id)&&validId(m.mutationId)&&m.baseVersion>=0,"待发送事项结构无效。"); return m;
        }
    }
    public static final class Pending {
        public Mutation mutation;
        public long localRevision;
        Pending copy() { Pending p=new Pending(); p.mutation=mutation.copy(); p.localRevision=localRevision; return p; }
    }
    public static final class CloudRecord {
        public String id;
        public long version;
        public boolean deleted;
        public TaskData data;
        public static CloudRecord fromJson(JSONObject j) throws JSONException {
            CloudRecord r=new CloudRecord(); r.id=string(j,"id"); r.version=integer(j,"version"); r.deleted=bool(j,"deleted");
            require(validId(r.id)&&r.version>0,"云端事项结构无效。"); r.data=TaskData.fromJson(j.getJSONObject("data"),r.deleted); return r;
        }
    }
    public static final class Result {
        public String id;
        public String mutationId;
        public String status;
        public long version;
    }
    public static final class Response {
        public String accountId;
        public String accountLabel;
        public String serverTime;
        public List<Result> results=new ArrayList<>();
        public List<CloudRecord> items=new ArrayList<>();
        public static Response fromJson(JSONObject j) throws JSONException {
            Response r=new Response(); r.accountId=string(j,"account_id"); validateAccount(r.accountId);
            r.accountLabel=string(j,"account_label"); r.serverTime=string(j,"server_time");
            JSONArray rows=j.getJSONArray("items"), results=j.getJSONArray("results");
            require(rows.length()<=10000,"云端事项超过预览版上限。");
            Set<String> ids=new HashSet<>(), receipts=new HashSet<>();
            for(int i=0;i<rows.length();i++) { CloudRecord item=CloudRecord.fromJson(rows.getJSONObject(i)); require(ids.add(item.id),"云端事项重复。"); r.items.add(item); }
            for(int i=0;i<results.length();i++) {
                JSONObject json=results.getJSONObject(i); Result result=new Result();
                result.id=string(json,"id"); result.mutationId=string(json,"mutation_id"); result.status=string(json,"status"); result.version=integer(json,"version");
                require(validId(result.id)&&validId(result.mutationId)&&result.version>=0
                        &&Arrays.asList("applied","conflict").contains(result.status)&&receipts.add(result.mutationId),"云端确认结构无效。");
                r.results.add(result);
            }
            return r;
        }
    }

    public static final class State {
        public int format = 1;
        public String deviceId = newId();
        public String accountId;
        public String accountLabel = "未连接";
        public String lastSync;
        public Map<String,Record> records = new LinkedHashMap<>();
        public List<Pending> pending = new ArrayList<>();
        public State copy() {
            State s=new State(); s.format=format; s.deviceId=deviceId; s.accountId=accountId; s.accountLabel=accountLabel; s.lastSync=lastSync;
            for(Record r:records.values())s.records.put(r.id,r.copy()); for(Pending p:pending)s.pending.add(p.copy()); return s;
        }
        public List<Record> visible() {
            List<Record> rows=new ArrayList<>(); for(Record r:records.values())if(!r.deleted)rows.add(r.copy()); return rows;
        }
        public int dirtyCount() { int n=0; for(Record r:records.values())if(r.dirty)n++; return n; }
        /** Returns true only when a stale editor needed a visible conflict copy. */
        public boolean edit(String id, TaskData data, long expectedRevision) {
            data.validate(); Record old=id==null?null:records.get(id);
            if(old!=null) {
                if(expectedRevision>=0 && expectedRevision!=old.revision) {
                    TaskData copy=data.copy(); copy.title=copyTitle(copy.title,"（编辑副本）"); add(copy); return true;
                }
                if(old.data.equals(data)&&!old.deleted)return false;
                old.data=data.copy(); old.deleted=false; old.dirty=true; old.revision++;
            } else { add(data); }
            return false;
        }
        private void add(TaskData data) { Record r=new Record(); r.data=data.copy(); records.put(r.id,r); }
        public void delete(String id) { Record r=records.get(id); if(r!=null&&!r.deleted) { r.deleted=true; r.dirty=true; r.revision++; } }
        public List<Mutation> prepareBatch() throws JSONException {
            Set<String> queued=new HashSet<>(); for(Pending p:pending)queued.add(p.mutation.id);
            List<Record> sorted=new ArrayList<>(records.values()); sorted.sort(Comparator.comparing(r->r.id));
            for(Record r:sorted)if(r.dirty&&!queued.contains(r.id)) {
                Pending p=new Pending(); Mutation m=new Mutation(); m.id=r.id; m.mutationId=newId(); m.baseVersion=r.version; m.deleted=r.deleted; m.data=r.data.copy();
                p.mutation=m; p.localRevision=r.revision; pending.add(p);
            }
            List<Mutation> batch=new ArrayList<>(); long bytes=2048;
            for(Pending p:pending) {
                int length=p.mutation.toJson().toString().getBytes(StandardCharsets.UTF_8).length+1;
                if(bytes+length>850000) { require(!batch.isEmpty(),"单条事项内容过长，暂时无法同步。"); break; }
                batch.add(p.mutation.copy()); bytes+=length; if(batch.size()==100)break;
            }
            return batch;
        }
        public JSONObject toJson() throws JSONException {
            JSONObject j=new JSONObject().put("format",format).put("device_id",deviceId).put("account_label",accountLabel);
            putNullable(j,"account_id",accountId); putNullable(j,"last_sync",lastSync);
            JSONArray rows=new JSONArray(), queue=new JSONArray(); for(Record r:records.values())rows.put(r.toJson());
            for(Pending p:pending)queue.put(new JSONObject().put("mutation",p.mutation.toJson()).put("local_revision",p.localRevision));
            return j.put("records",rows).put("pending",queue);
        }
        public static State fromJson(JSONObject j) throws JSONException {
            State s=new State(); require(integer(j,"format")==1,"本机数据版本不受支持。"); s.deviceId=string(j,"device_id");
            require(validId(s.deviceId),"本机设备标识无效。"); s.accountId=nullableString(j,"account_id"); validateAccount(s.accountId);
            s.accountLabel=string(j,"account_label"); s.lastSync=nullableString(j,"last_sync");
            JSONArray records=j.getJSONArray("records"),pending=j.getJSONArray("pending");
            Set<String> ids=new HashSet<>(), mutations=new HashSet<>();
            for(int i=0;i<records.length();i++) { Record r=Record.fromJson(records.getJSONObject(i)); require(!s.records.containsKey(r.id),"本机事项标识重复。"); s.records.put(r.id,r); }
            for(int i=0;i<pending.length();i++) {
                JSONObject json=pending.getJSONObject(i); Pending p=new Pending(); p.mutation=Mutation.fromJson(json.getJSONObject("mutation")); p.localRevision=integer(json,"local_revision");
                Record local=s.records.get(p.mutation.id);
                require(ids.add(p.mutation.id)&&mutations.add(p.mutation.mutationId)&&p.localRevision>=0&&local!=null
                        &&p.localRevision<=local.revision,"本机待发送队列无效。"); s.pending.add(p);
            }
            return s;
        }
    }

    public static final class Merge {
        public State state;
        public int conflicts;
    }
    /** All validation and edits run on a copy: rejected responses cannot partially change the caller. */
    public static Merge merge(State source, Response response, List<Mutation> sent) {
        validateAccount(response.accountId); require(response.accountId!=null,"云端缺少账号。");
        require(source.accountId==null||source.accountId.equals(response.accountId),"本机日历已绑定另一个账号，请使用原来的同步码。");
        Map<String,CloudRecord> remote=new LinkedHashMap<>();
        for(CloudRecord r:response.items) { require(validId(r.id)&&r.version>0&&!remote.containsKey(r.id),"云端快照无效。"); if(!r.deleted)r.data.validate(); remote.put(r.id,r); }
        for(Record r:source.records.values())if(r.version>0)require(remote.containsKey(r.id)&&remote.get(r.id).version>=r.version,
                "云端快照缺少已知事项或版本较旧，本机日程已保留。");
        require(response.results.size()==sent.size(),"同步确认数量不完整。");
        Map<String,Mutation> sentIds=new LinkedHashMap<>(); for(Mutation m:sent)require(sentIds.put(m.mutationId,m)==null,"重复的发送标识。");
        Map<String,Pending> queued=new LinkedHashMap<>(); for(Pending p:source.pending)queued.put(p.mutation.mutationId,p);
        Set<String> resultIds=new HashSet<>();
        for(Result result:response.results) {
            Mutation sentMutation=sentIds.get(result.mutationId); Pending p=queued.get(result.mutationId); CloudRecord r=remote.get(result.id);
            require(resultIds.add(result.mutationId)&&sentMutation!=null&&p!=null&&sentMutation.id.equals(result.id)
                    &&p.mutation.id.equals(result.id)&&result.version>=0&&Arrays.asList("applied","conflict").contains(result.status),"同步确认与请求不一致。");
            require(result.version==0 || (r!=null && r.version>=result.version),"同步确认缺少云端事项。");
            require(!"applied".equals(result.status)||result.version>0,"同步确认版本无效。");
        }
        State s=source.copy(); int conflicts=0; Set<String> handled=new HashSet<>();
        for(Result result:response.results) {
            Pending p=queued.get(result.mutationId); Record local=s.records.get(result.id); CloudRecord r=remote.get(result.id);
            if("applied".equals(result.status)) {
                if(local==null||local.revision==p.localRevision)adopt(s,r);
                else if(r.version==result.version) { local.version=r.version; local.dirty=true; }
                else { conflicts+=preserveConflict(s,local,r); }
            } else { if(local!=null)conflicts+=preserveConflict(s,local,r); else if(r!=null)adopt(s,r); }
            s.pending.removeIf(item->item.mutation.mutationId.equals(result.mutationId)); handled.add(result.id);
        }
        Set<String> stillPending=new HashSet<>(); for(Pending p:s.pending)stillPending.add(p.mutation.id);
        for(CloudRecord r:response.items)if(!handled.contains(r.id)&&!stillPending.contains(r.id)) {
            Record local=s.records.get(r.id);
            if(local!=null&&local.dirty) { if(r.version>local.version)conflicts+=preserveConflict(s,local,r); }
            else if(local==null||local.version<=r.version)adopt(s,r);
        }
        s.accountId=response.accountId; s.accountLabel=response.accountLabel; s.lastSync=response.serverTime;
        Merge result=new Merge(); result.state=s; result.conflicts=conflicts; return result;
    }
    private static void adopt(State s, CloudRecord cloud) {
        Record old=s.records.get(cloud.id),r=new Record(); r.id=cloud.id; r.data=cloud.data.copy(); r.deleted=cloud.deleted;
        r.version=cloud.version; r.dirty=false;
        boolean changed=old==null||!old.data.equals(cloud.data)||old.deleted!=cloud.deleted;
        r.revision=(old==null?0:old.revision)+(changed?1:0); s.records.put(r.id,r);
    }
    private static int preserveConflict(State s, Record local, CloudRecord remote) {
        if(local!=null&&!local.deleted) { TaskData d=local.data.copy(); d.title=copyTitle(d.title,"（冲突副本）"); s.add(d); }
        if(remote!=null)adopt(s,remote); else if(local!=null)s.records.remove(local.id); return 1;
    }
}
