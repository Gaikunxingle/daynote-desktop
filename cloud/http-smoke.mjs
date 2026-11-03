// Exercise the deployed RPC with disposable accounts only. Never accepts real user data.
import { readFile, writeFile } from 'node:fs/promises';
import { randomBytes } from 'node:crypto';
import assert from 'node:assert/strict';

const url = 'https://c--ff542ec0-79c6-40d2-9610-34eb964ccd22-prod.lovable.cloud/rest/v1';
const publishable = 'sb_publishable_rlWdj3LzNurnQW9c--QvDA_udvomPd1';
const credentials = JSON.parse(await readFile(new URL('../.private/qa-credentials.json', import.meta.url), 'utf8'));
const results = [];
const id = () => randomBytes(16).toString('hex');
const headers = { apikey: publishable, 'Content-Type': 'application/json' };
const data = (title) => ({ title, notes: 'Synthetic sync verification only', due_at: '2026-11-10T06:00:00Z', has_time: true, reminder_minutes: 10, completed: false, end_at: '2026-11-10T07:00:00Z', color_id: 'blue', is_deadline: false });
async function raw(key, changes = [], device = 'http-smoke-device') {
  const response = await fetch(url + '/rpc/daynote_sync_v1', { method: 'POST', headers, body: JSON.stringify({ p_key: key, p_device: device, p_changes: changes }), signal: AbortSignal.timeout(30000) });
  const body = await response.json();
  return { status: response.status, body };
}
async function sync(key, changes = [], device) {
  const r = await raw(key, changes, device);
  assert.equal(r.status, 200, 'RPC must succeed; received HTTP ' + r.status + ' code ' + r.body?.code);
  assert(Array.isArray(r.body.items));
  return r.body;
}
async function check(name, action) { await action(); results.push({ name, passed: true }); console.log('PASS ' + name); }
const a = credentials.a.key, b = credentials.b.key;
let event, first, mutation;
await check('Invalid key rejected without credential leakage', async () => {
  const badKey = randomBytes(32).toString('base64url');
  const r = await raw(badKey); assert([400,401,403].includes(r.status)); assert(!JSON.stringify(r.body).includes(badKey));
});
await check('Separate accounts return separate identities', async () => {
  const x=await sync(a), y=await sync(b); assert.notEqual(x.account_id,y.account_id);
});
await check('Create on device one; read on device two', async () => {
  event=id(); mutation={id:event,mutation_id:id(),base_version:0,deleted:false,data:data('同步测试：初始事项')};
  first=await sync(a,[mutation],'device-one'); assert.equal(first.results[0].status,'applied');
  const second=await sync(a,[],'device-two'); assert.equal(second.items.find(x=>x.id===event).data.title,mutation.data.title);
});
await check('Other account cannot read the event', async () => {
  assert(!(await sync(b)).items.some(x=>x.id===event));
});
await check('Retry is idempotent; mutation payload cannot be replaced', async () => {
  const retried=await sync(a,[mutation]); assert.equal(retried.results[0].version,1); assert.equal(retried.items.find(x=>x.id===event).version,1);
  const r=await raw(a,[{...mutation,data:data('must reject changed retry')}]); assert.equal(r.status,400);
});
await check('Concurrent updates have one winner and one conflict', async () => {
  const [x,y]=await Promise.all([sync(a,[{...mutation,mutation_id:id(),base_version:1,data:data('设备甲修改')}],'device-one'),sync(a,[{...mutation,mutation_id:id(),base_version:1,data:data('设备乙修改')}],'device-two')]);
  assert.deepEqual([x.results[0].status,y.results[0].status].sort(),['applied','conflict']);
  assert.equal((await sync(a)).items.find(x=>x.id===event).version,2);
});
await check('Delete reaches other devices; stale edit cannot resurrect it', async () => {
  const result=await sync(a,[{id:event,mutation_id:id(),base_version:2,deleted:true,data:{}}]); assert.equal(result.results[0].status,'applied');
  const tombstone=(await sync(a,[],'device-two')).items.find(x=>x.id===event); assert.equal(tombstone.deleted,true);
  const stale=await sync(a,[{...mutation,mutation_id:id(),base_version:2}]); assert.equal(stale.results[0].status,'conflict'); assert.equal(stale.items.find(x=>x.id===event).deleted,true);
});
await check('Same record ID in another account remains isolated', async () => {
  const other=await sync(b,[{...mutation,mutation_id:id(),data:data('另一个人的独立日程')}]); assert.equal(other.results[0].status,'applied');
  assert.equal((await sync(a)).items.find(x=>x.id===event).deleted,true);
});
await check('All-day date is preserved without timezone shift', async () => {
  const eventId=id(); const payload={...data('全天事项'),due_at:'2026-11-11',end_at:null,has_time:false};
  const r=await sync(a,[{id:eventId,mutation_id:id(),base_version:0,deleted:false,data:payload}]);
  assert.equal(r.items.find(x=>x.id===eventId).data.due_at,'2026-11-11');
});
await check('Malformed timestamp is rejected atomically', async () => {
  const eventId=id(); const r=await raw(a,[{id:eventId,mutation_id:id(),base_version:0,deleted:false,data:{...data('bad'),due_at:'2026-99-99T25:00:00Z'}}]);
  assert.equal(r.status,400); assert(!(await sync(a)).items.some(x=>x.id===eventId));
});
await check('Direct table access is unavailable', async () => {
  for(const table of ['accounts','events','mutations']) {
    const r=await fetch(url+'/'+table+'?select=*',{headers,signal:AbortSignal.timeout(30000)}); assert(r.status>=400);
  }
});
const report={tested_at:new Date().toISOString(),kind:'deployed RPC with synthetic disposable accounts',tests:results};
await writeFile(new URL('../qa/native-sync-http.json',import.meta.url),JSON.stringify(report,null,2));
console.log('All '+results.length+' deployed HTTP checks passed. Report contains no credentials.');
