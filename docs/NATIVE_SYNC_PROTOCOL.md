# Daynote native sync preview protocol

This is a two-person private preview. Each person gets a separate random 256-bit sync key. It is a bearer credential: keep it in Keychain / Windows DPAPI, never source, exports, screenshots, or logs. Keys map to isolated cloud accounts. Do not let a different account reuse a profile's tasks or pending changes.

Endpoint: `https://c--ff542ec0-79c6-40d2-9610-34eb964ccd22-prod.lovable.cloud/rest/v1/rpc/daynote_sync_v1`

Headers: `apikey: sb_publishable_rlWdj3LzNurnQW9c--QvDA_udvomPd1`, `Content-Type: application/json`. The publishable key is intentionally public; it grants no direct table access. No Authorization header is required.

Request:
```json
{"p_key":"<private sync key>","p_device":"<stable random device ID>","p_changes":[{"id":"32-character UUID hex","mutation_id":"32-character UUID hex","base_version":0,"deleted":false,"data":{"title":"Example","notes":"","due_at":"2026-11-03T06:00:00Z","has_time":true,"reminder_minutes":10,"completed":false,"end_at":null,"color_id":"blue","is_deadline":false}}]}
```

Response:
```json
{"account_id":"uuid","account_label":"我的日历","server_time":"ISO8601","results":[{"id":"...","mutation_id":"...","status":"applied","version":1}],"items":[{"id":"...","version":1,"deleted":false,"data":{},"updated_at":"ISO8601"}]}
```

- First validate a new key with an EMPTY `p_changes` before accepting it or uploading anything. Response gives account ID and display label. Private accounts are provisioned separately by the owner, never automatically by a public endpoint.
- All records including tombstones are returned (private preview max 10000 records per account, limit checked server-side; no silent truncation). Changes per call <=100. Max request ~1 MB. Clients may send remaining changes next call.
- `base_version=0` creates a record; updates/deletes require the exact current version. Conflicts return `status=conflict`, current `version` (0 if absent), and the current item in `items`. Never silently overwrite a conflict. Preserve a conflicting local edit as a new visible copy with a new ID and title suffix `（冲突副本）`; for delete conflicts keep the newer cloud item and show a notice. User edits occurring during an in-flight request must survive response merging.
- `mutation_id` uniquely identifies a persisted request. Replaying exactly the same mutation is idempotent. Reusing it with different data is rejected. Retries MUST retain the same mutation ID until acknowledged.
- `deleted=true` marks a tombstone, not a physical delete. Its `data` may be the prior full data or `{}`; clients must not expose tombstones as visible tasks.
- `due_at`/`end_at`: null, or an ISO8601 instant WITH `Z`/offset for timed events. All-day events use `yyyy-MM-dd` (no UTC conversion). Unscheduled tasks have null dates. Dates display in device local time for timed items. Use invariant formatting; do not sync .NET `/Date(...)` strings.
- `id` and `mutation_id` are 32 lowercase hexadecimal characters. `p_device` is a stable random ID. `version` is a server integer. `reminder_minutes` is -1 (off) or 0..525600. Allowed colors: blue, teal, purple, orange, green, red, pink, gray.
- Credentials, window/layout preferences, and last-notified state stay local. Pulling unchanged records must not re-arm sent notifications. Changing timing must reschedule local notifications.
- Offline operation and pending writes persist atomically. A network/auth failure is NEVER an empty calendar. No requests on the UI thread. Sync on launch/foreground, after edits, periodic while app active, and via a visible manual button. iOS background instant sync/push is not promised; notifications use locally scheduled OS notifications after data is fetched.
- Back up existing Windows data before first cloud binding. Never auto-upload the user's real calendar during development or testing. Use isolated fixture profiles.
