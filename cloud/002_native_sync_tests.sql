-- Run as the database owner after 001_native_sync.sql.
-- Everything below is rolled back, including fixture accounts and credentials.
-- No production calendar or account is read. Do not remove the final ROLLBACK.
BEGIN;

CREATE TEMPORARY TABLE daynote_test_context(key_a text, key_b text) ON COMMIT DROP;
INSERT INTO daynote_test_context VALUES(
    encode(extensions.gen_random_bytes(32),'hex'),
    encode(extensions.gen_random_bytes(32),'hex')
);
INSERT INTO daynote_private.accounts(label,key_hash)
  SELECT 'QA isolated A',extensions.digest(key_a,'sha256') FROM daynote_test_context
  UNION ALL SELECT 'QA isolated B',extensions.digest(key_b,'sha256') FROM daynote_test_context;
GRANT SELECT ON daynote_test_context TO anon;

DO $checks$
BEGIN
    IF has_schema_privilege('anon','daynote_private','USAGE')
       OR has_schema_privilege('authenticated','daynote_private','USAGE')
       OR has_table_privilege('anon','daynote_private.events','SELECT')
       OR has_table_privilege('authenticated','daynote_private.events','INSERT')
       OR has_table_privilege('anon','daynote_private.accounts','SELECT')
       OR has_table_privilege('authenticated','daynote_private.receipts','SELECT') THEN
        RAISE EXCEPTION 'FAIL: clients must not have private schema/table privileges';
    END IF;
    IF EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
              WHERE n.nspname='daynote_private' AND c.relname IN ('accounts','events','receipts')
                AND NOT c.relrowsecurity) THEN
        RAISE EXCEPTION 'FAIL: all private tables require RLS';
    END IF;
    IF NOT has_function_privilege('anon','public.daynote_sync_v1(text,text,jsonb)','EXECUTE') THEN
        RAISE EXCEPTION 'FAIL: anon requires the restricted RPC';
    END IF;
END;
$checks$;

SET LOCAL ROLE anon;
DO $tests$
DECLARE
    key_a text;
    key_b text;
    a jsonb;
    b jsonb;
    first jsonb;
    retried jsonb;
    change jsonb;
    changes jsonb;
    payload jsonb := '{"title":"Fixture only","notes":"No real schedule","due_at":"2026-11-03T14:00:00+08:00","has_time":true,"reminder_minutes":10,"completed":false,"end_at":"2026-11-03T15:00:00+08:00","color_id":"blue","is_deadline":false}'::jsonb;
    expected_failure boolean;
    bad jsonb;
BEGIN
    SELECT t.key_a,t.key_b INTO key_a,key_b FROM daynote_test_context t;
    a := public.daynote_sync_v1(key_a,'qa-device-a','[]');
    b := public.daynote_sync_v1(key_b,'qa-device-b','[]');
    IF a->>'account_id'=b->>'account_id' OR a->'items'<>'[]'::jsonb OR b->'items'<>'[]'::jsonb THEN
        RAISE EXCEPTION 'FAIL: empty isolated accounts';
    END IF;

    expected_failure := false;
    BEGIN
        PERFORM public.daynote_sync_v1(repeat('x',64),'qa-device-a','[]');
    EXCEPTION WHEN insufficient_privilege THEN expected_failure := true;
    END;
    IF NOT expected_failure THEN RAISE EXCEPTION 'FAIL: unknown key was accepted'; END IF;
    expected_failure := false;
    BEGIN
        PERFORM * FROM daynote_private.events;
    EXCEPTION WHEN insufficient_privilege THEN expected_failure := true;
    END;
    IF NOT expected_failure THEN RAISE EXCEPTION 'FAIL: direct data access was allowed'; END IF;

    change := jsonb_build_object('id',repeat('a',32),'mutation_id',repeat('1',32),
                               'base_version',0,'deleted',false,'data',payload);
    first := public.daynote_sync_v1(key_a,'qa-device-a',jsonb_build_array(change));
    IF first#>>'{results,0,status}'<>'applied' OR first#>>'{results,0,version}'<>'1'
       OR jsonb_array_length(first->'items')<>1 THEN RAISE EXCEPTION 'FAIL: create'; END IF;

    -- Exact retries from a different device must return the original receipt.
    retried := public.daynote_sync_v1(key_a,'qa-device-c',jsonb_build_array(change));
    IF retried->'results'<>first->'results' OR retried->'items'<>first->'items' THEN
        RAISE EXCEPTION 'FAIL: create retry was not idempotent';
    END IF;
    expected_failure := false;
    BEGIN
        PERFORM public.daynote_sync_v1(key_a,'qa-device-a',
          jsonb_build_array(jsonb_set(change,'{data,title}','"Changed receipt content"')));
    EXCEPTION WHEN invalid_parameter_value THEN expected_failure := true;
    END;
    IF NOT expected_failure THEN RAISE EXCEPTION 'FAIL: reused mutation with changed content'; END IF;

    -- Another account can use the same record/mutation IDs without reading A.
    b := public.daynote_sync_v1(key_b,'qa-device-b',jsonb_build_array(
      jsonb_set(change,'{data,title}','"Independent B"')));
    IF b#>>'{items,0,data,title}'<>'Independent B' OR jsonb_array_length(b->'items')<>1 THEN
        RAISE EXCEPTION 'FAIL: account B create';
    END IF;
    a := public.daynote_sync_v1(key_a,'qa-device-a','[]');
    IF a#>>'{items,0,data,title}'<>'Fixture only' THEN RAISE EXCEPTION 'FAIL: cross-account overwrite'; END IF;

    -- A stale writer gets a conflict, and a conflict retry is also idempotent.
    change := jsonb_set(change,'{mutation_id}',to_jsonb(repeat('2',32)));
    first := public.daynote_sync_v1(key_a,'qa-device-a',jsonb_build_array(change));
    retried := public.daynote_sync_v1(key_a,'qa-device-b',jsonb_build_array(change));
    IF first#>>'{results,0,status}'<>'conflict' OR first#>>'{results,0,version}'<>'1'
       OR retried->'results'<>first->'results' OR first#>>'{items,0,version}'<>'1' THEN
        RAISE EXCEPTION 'FAIL: stale write conflict';
    END IF;

    -- Delete is durable and does not physically remove the record.
    change := jsonb_build_object('id',repeat('a',32),'mutation_id',repeat('3',32),
                               'base_version',1,'deleted',true,'data','{}'::jsonb);
    a := public.daynote_sync_v1(key_a,'qa-device-a',jsonb_build_array(change));
    IF a#>>'{results,0,version}'<>'2' OR a#>>'{items,0,deleted}'<>'true'
       OR a#>>'{items,0,data,title}'<>'Fixture only' THEN RAISE EXCEPTION 'FAIL: delete tombstone'; END IF;
    change := jsonb_build_object('id',repeat('a',32),'mutation_id',repeat('4',32),
                               'base_version',1,'deleted',false,'data',payload);
    a := public.daynote_sync_v1(key_a,'qa-device-b',jsonb_build_array(change));
    IF a#>>'{results,0,status}'<>'conflict' OR a#>>'{items,0,deleted}'<>'true' THEN
        RAISE EXCEPTION 'FAIL: stale update resurrected deletion';
    END IF;
    -- Explicit restore at the tombstone's current version is allowed.
    change := jsonb_set(jsonb_set(change,'{base_version}','2'),'{mutation_id}',to_jsonb(repeat('5',32)));
    a := public.daynote_sync_v1(key_a,'qa-device-a',jsonb_build_array(change));
    IF a#>>'{results,0,status}'<>'applied' OR a#>>'{items,0,version}'<>'3'
       OR a#>>'{items,0,deleted}'<>'false' THEN RAISE EXCEPTION 'FAIL: explicit restore'; END IF;

    -- Invalid date/type/schema cases must fail without mutating any records.
    FOREACH bad IN ARRAY ARRAY[
        jsonb_set(payload,'{due_at}','"2026-02-31T14:00:00Z"'),
        jsonb_set(payload,'{due_at}','"2026-11-03T14:00:00"'),
        jsonb_set(payload,'{due_at}','"/Date(123456789)/"'),
        jsonb_set(payload,'{end_at}','"2026-11-02T14:00:00Z"'),
        jsonb_set(payload,'{title}','""'),
        jsonb_set(payload,'{completed}','"false"'),
        jsonb_set(payload,'{reminder_minutes}','525601'),
        jsonb_set(payload,'{reminder_minutes}','1.5'),
        jsonb_set(payload,'{color_id}','"unknown"'),
        payload || '{"owner_id":"someone-else"}'::jsonb,
        payload - 'notes'
    ] LOOP
        expected_failure := false;
        BEGIN
            PERFORM public.daynote_sync_v1(key_a,'qa-device-a',jsonb_build_array(
              jsonb_build_object('id',repeat('c',32),'mutation_id',replace(gen_random_uuid()::text,'-',''),
                                 'base_version',0,'deleted',false,'data',bad)));
        EXCEPTION WHEN invalid_parameter_value THEN expected_failure := true;
        END;
        IF NOT expected_failure THEN RAISE EXCEPTION 'FAIL: invalid payload accepted'; END IF;
    END LOOP;

    -- All-day dates stay literal dates, and unscheduled records retain null dates.
    change := jsonb_build_object('id',repeat('d',32),'mutation_id',repeat('6',32),'base_version',0,
        'deleted',false,'data',payload || '{"has_time":false,"due_at":"2026-11-03","end_at":null}'::jsonb);
    a := public.daynote_sync_v1(key_a,'qa-device-a',jsonb_build_array(change));
    IF NOT EXISTS(SELECT 1 FROM jsonb_array_elements(a->'items') x
                  WHERE x->>'id'=repeat('d',32) AND x#>>'{data,due_at}'='2026-11-03') THEN
        RAISE EXCEPTION 'FAIL: all-day date changed';
    END IF;
    change := jsonb_build_object('id',repeat('e',32),'mutation_id',repeat('7',32),'base_version',0,
        'deleted',false,'data',payload || '{"due_at":null,"end_at":null}'::jsonb);
    a := public.daynote_sync_v1(key_a,'qa-device-a',jsonb_build_array(change));
    IF NOT EXISTS(SELECT 1 FROM jsonb_array_elements(a->'items') x
                  WHERE x->>'id'=repeat('e',32) AND x#>'{data,due_at}'='null'::jsonb) THEN
        RAISE EXCEPTION 'FAIL: unscheduled task changed';
    END IF;

    -- Whole batch atomicity: an invalid second change rolls back the first.
    changes := jsonb_build_array(
      jsonb_build_object('id',repeat('f',32),'mutation_id',repeat('8',32),'base_version',0,'deleted',false,'data',payload),
      jsonb_build_object('id','not-an-id','mutation_id',repeat('9',32),'base_version',0,'deleted',false,'data',payload));
    expected_failure := false;
    BEGIN
        PERFORM public.daynote_sync_v1(key_a,'qa-device-a',changes);
    EXCEPTION WHEN invalid_parameter_value THEN expected_failure := true;
    END;
    a := public.daynote_sync_v1(key_a,'qa-device-a','[]');
    IF NOT expected_failure OR EXISTS(SELECT 1 FROM jsonb_array_elements(a->'items') x WHERE x->>'id'=repeat('f',32)) THEN
        RAISE EXCEPTION 'FAIL: invalid batch was partially applied';
    END IF;

    expected_failure := false;
    BEGIN
        PERFORM public.daynote_sync_v1(key_a,'qa-device-a','{}');
    EXCEPTION WHEN invalid_parameter_value THEN expected_failure := true;
    END;
    IF NOT expected_failure THEN RAISE EXCEPTION 'FAIL: object changes accepted'; END IF;
END;
$tests$;
RESET ROLE;

DO $disabled$
DECLARE
    key_a text;
    expected_failure boolean := false;
BEGIN
    SELECT t.key_a INTO key_a FROM daynote_test_context t;
    UPDATE daynote_private.accounts SET disabled=true WHERE key_hash=extensions.digest(key_a,'sha256');
    BEGIN
        PERFORM public.daynote_sync_v1(key_a,'qa-device-a','[]');
    EXCEPTION WHEN insufficient_privilege THEN expected_failure := true;
    END;
    IF NOT expected_failure THEN RAISE EXCEPTION 'FAIL: disabled account accepted'; END IF;
END;
$disabled$;

SELECT 'PASS: anonymous RPC, key denial, account isolation, idempotency, conflicts, tombstones, restore, dates, validation and batch atomicity; all fixtures rolled back.' AS result;
ROLLBACK;
