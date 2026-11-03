# Native sync backend

Apply `001_native_sync.sql` with the database-owner connection. It adds one private schema and the public `daynote_sync_v1` RPC; it does not change website authentication or signup settings. The `pgcrypto` extension is expected in `extensions`, as in the existing deployment.

Apply `002_native_sync_tests.sql` as one complete transaction/script after the migration. Its two random fixture accounts are rolled back. An error must abort the transaction; never retain the fixture state or remove `ROLLBACK`. The tests call the RPC under the `anon` role and verify permission boundaries. `http-smoke.mjs` separately tests the actual network endpoint.

## Account provisioning

Only the owner provisions accounts. Generate 32 cryptographically random bytes and encode them as base64url (43 characters) or hexadecimal (64 characters). Compute SHA-256 locally. Send only the hex hash to the SQL tool:

```sql
INSERT INTO daynote_private.accounts(label,key_hash)
VALUES ('我的日历',decode('<64-character SHA-256 hex digest>','hex'))
RETURNING id,label;
```

The actual key belongs only in the owner's private delivery material and the clients' OS credential stores. Never place keys in source, committed files, logs, URLs, exported calendars, or screenshots. A key gives full read/write access to exactly one account. Each person must have a different key; devices belonging to the same person use the same key.

`accounts.id` is UUID, `label` is text, `key_hash` is 32-byte `bytea`, and `disabled` defaults to false. To revoke a leaked key, set `disabled=true` for that exact account ID, then rotate its hash through the owner connection and re-enable it. Updating the hash retains the account ID and all calendar records. Do not create a replacement account when rotating a key.

## Security and sync behavior

- No anonymous or authenticated role has private-schema or table access. RLS is enabled without client policies. The RPC runs as its owner with an empty search path and validates the high-entropy key before accessing records.
- Account-scoped advisory locks serialize transactions. Exact mutation receipts make retries idempotent. Reusing a mutation ID with different content is an error, not an overwrite.
- Stale base versions return a conflict and the current records. Deletes remain versioned tombstones. An explicit update using a tombstone's current version may restore it.
- Requests accept at most 100 mutations and 1 MiB of mutation JSON. Accounts accept at most 10,000 records including tombstones. The response is a complete snapshot; no implicit truncation or pagination occurs.
- Unknown/disabled keys return SQLSTATE `42501`; invalid input returns `22023`; capacity limits return `54000`. Error messages never echo credentials or schedule content.
- This preview has no public registration, sharing, email, password recovery or background push service. Keep requests out of access logs. Possession of a key is the authentication factor; support key revocation if a device or key is lost.
- Keep a recoverable local calendar export. This migration does not create managed backups or promise free-tier uptime.

The wire contract is in `../docs/NATIVE_SYNC_PROTOCOL.md`.
