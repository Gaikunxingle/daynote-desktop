-- Daynote native private preview. Apply as the database owner.
-- This migration is additive and does not modify website auth or existing tables.
-- Provision private account keys separately; never commit their plaintext.
BEGIN;

CREATE SCHEMA IF NOT EXISTS daynote_private;
REVOKE ALL ON SCHEMA daynote_private FROM PUBLIC, anon, authenticated;

CREATE TABLE IF NOT EXISTS daynote_private.accounts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    label text NOT NULL CHECK (char_length(label) BETWEEN 1 AND 64),
    key_hash bytea NOT NULL UNIQUE CHECK (octet_length(key_hash) = 32),
    disabled boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS daynote_private.events (
    account_id uuid NOT NULL REFERENCES daynote_private.accounts(id) ON DELETE CASCADE,
    id text NOT NULL CHECK (id ~ '^[0-9a-f]{32}$'),
    version integer NOT NULL CHECK (version > 0),
    deleted boolean NOT NULL DEFAULT false,
    data jsonb NOT NULL CHECK (jsonb_typeof(data) = 'object'),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, id)
);

CREATE TABLE IF NOT EXISTS daynote_private.receipts (
    account_id uuid NOT NULL REFERENCES daynote_private.accounts(id) ON DELETE CASCADE,
    mutation_id text NOT NULL CHECK (mutation_id ~ '^[0-9a-f]{32}$'),
    request_hash bytea NOT NULL CHECK (octet_length(request_hash) = 32),
    result jsonb NOT NULL CHECK (jsonb_typeof(result) = 'object'),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, mutation_id)
);

ALTER TABLE daynote_private.accounts ENABLE ROW LEVEL SECURITY;
ALTER TABLE daynote_private.events ENABLE ROW LEVEL SECURITY;
ALTER TABLE daynote_private.receipts ENABLE ROW LEVEL SECURITY;
-- No client policies: the single validated RPC is the only client access path.
REVOKE ALL ON ALL TABLES IN SCHEMA daynote_private FROM PUBLIC, anon, authenticated;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA daynote_private FROM PUBLIC, anon, authenticated;
ALTER DEFAULT PRIVILEGES IN SCHEMA daynote_private REVOKE ALL ON TABLES FROM PUBLIC, anon, authenticated;
ALTER DEFAULT PRIVILEGES IN SCHEMA daynote_private REVOKE ALL ON FUNCTIONS FROM PUBLIC, anon, authenticated;

CREATE OR REPLACE FUNCTION public.daynote_sync_v1(
    p_key text,
    p_device text,
    p_changes jsonb DEFAULT '[]'::jsonb
) RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = ''
SET statement_timeout = '15s'
AS $function$
DECLARE
    v_account daynote_private.accounts%ROWTYPE;
    v_change jsonb;
    v_data jsonb;
    v_event daynote_private.events%ROWTYPE;
    v_receipt daynote_private.receipts%ROWTYPE;
    v_request_hash bytea;
    v_base integer;
    v_current integer;
    v_new_version integer;
    v_count integer;
    v_deleted boolean;
    v_has_time boolean;
    v_due text;
    v_end text;
    v_result jsonb;
    v_results jsonb := '[]'::jsonb;
    v_items jsonb;
    v_when timestamptz;
BEGIN
    -- Do not echo supplied credentials, payloads, or other account metadata.
    IF p_key IS NULL OR p_key !~ '^[A-Za-z0-9_-]{43,128}$' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Invalid or disabled sync key';
    END IF;

    SELECT * INTO v_account FROM daynote_private.accounts
      WHERE key_hash = extensions.digest(p_key, 'sha256') AND NOT disabled;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Invalid or disabled sync key';
    END IF;

    IF p_device IS NULL OR p_device !~ '^[A-Za-z0-9_-]{8,128}$' THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Invalid device identifier';
    END IF;
    IF jsonb_typeof(p_changes) IS DISTINCT FROM 'array' THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Changes must be an array';
    END IF;
    IF jsonb_array_length(p_changes) > 100 OR octet_length(p_changes::text) > 1048576 THEN
        RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Sync request is too large';
    END IF;

    -- Serializes writers for one account, including idempotency receipts. A hash
    -- collision only serializes unrelated accounts; it never grants access.
    PERFORM pg_advisory_xact_lock(hashtextextended(v_account.id::text, 0));
    -- Recheck under the account lock so a disabled account cannot keep writing.
    SELECT * INTO v_account FROM daynote_private.accounts
      WHERE id = v_account.id AND key_hash = extensions.digest(p_key, 'sha256') AND NOT disabled
      FOR SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'Invalid or disabled sync key';
    END IF;

    SELECT count(*) INTO v_count FROM daynote_private.events WHERE account_id = v_account.id;
    IF v_count > 10000 THEN
        RAISE EXCEPTION USING ERRCODE = '54000', MESSAGE = 'Calendar exceeds preview record limit';
    END IF;

    FOR v_change IN SELECT value FROM jsonb_array_elements(p_changes)
    LOOP
        IF jsonb_typeof(v_change) IS DISTINCT FROM 'object'
           OR NOT (v_change ?& ARRAY['id','mutation_id','base_version','deleted','data']) THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Invalid change envelope';
        END IF;
        IF EXISTS (SELECT 1 FROM jsonb_object_keys(v_change) AS field(name)
                   WHERE name <> ALL(ARRAY['id','mutation_id','base_version','deleted','data'])) THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Unexpected change field';
        END IF;
        IF jsonb_typeof(v_change->'id') IS DISTINCT FROM 'string'
           OR (v_change->>'id') !~ '^[0-9a-f]{32}$'
           OR jsonb_typeof(v_change->'mutation_id') IS DISTINCT FROM 'string'
           OR (v_change->>'mutation_id') !~ '^[0-9a-f]{32}$'
           OR jsonb_typeof(v_change->'deleted') IS DISTINCT FROM 'boolean'
           OR jsonb_typeof(v_change->'base_version') IS DISTINCT FROM 'number'
           OR (v_change->>'base_version') !~ '^(0|[1-9][0-9]{0,9})$' THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Invalid change identifier or version';
        END IF;
        IF (v_change->>'base_version')::numeric > 2147483646 THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Invalid base version';
        END IF;
        v_base := (v_change->>'base_version')::integer;
        v_deleted := (v_change->>'deleted')::boolean;
        v_data := v_change->'data';
        IF jsonb_typeof(v_data) IS DISTINCT FROM 'object' THEN
            RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Calendar data must be an object';
        END IF;

        -- A delete can carry an empty object; a non-empty delete must still have
        -- a valid complete payload. Empty tombstones preserve prior data below.
        IF NOT (v_deleted AND v_data = '{}'::jsonb) THEN
            IF NOT (v_data ?& ARRAY['title','notes','due_at','has_time','reminder_minutes',
                                   'completed','end_at','color_id','is_deadline'])
               OR EXISTS (SELECT 1 FROM jsonb_object_keys(v_data) AS field(name)
                          WHERE name <> ALL(ARRAY['title','notes','due_at','has_time','reminder_minutes',
                                                  'completed','end_at','color_id','is_deadline'])) THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Invalid calendar fields';
            END IF;
            IF jsonb_typeof(v_data->'title') IS DISTINCT FROM 'string'
               OR char_length(btrim(v_data->>'title')) NOT BETWEEN 1 AND 200
               OR char_length(v_data->>'title') > 200
               OR jsonb_typeof(v_data->'notes') IS DISTINCT FROM 'string'
               OR char_length(v_data->>'notes') > 20000
               OR jsonb_typeof(v_data->'has_time') IS DISTINCT FROM 'boolean'
               OR jsonb_typeof(v_data->'completed') IS DISTINCT FROM 'boolean'
               OR jsonb_typeof(v_data->'is_deadline') IS DISTINCT FROM 'boolean'
               OR jsonb_typeof(v_data->'reminder_minutes') IS DISTINCT FROM 'number'
               OR (v_data->>'reminder_minutes') !~ '^(-1|0|[1-9][0-9]{0,5})$'
               OR jsonb_typeof(v_data->'color_id') IS DISTINCT FROM 'string'
               OR (v_data->>'color_id') <> ALL(ARRAY['blue','teal','purple','orange','green','red','pink','gray']) THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Invalid calendar value';
            END IF;
            IF (v_data->>'reminder_minutes')::integer > 525600 THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Invalid reminder interval';
            END IF;
            IF jsonb_typeof(v_data->'due_at') NOT IN ('string','null')
               OR jsonb_typeof(v_data->'end_at') NOT IN ('string','null') THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Invalid calendar date type';
            END IF;
            v_has_time := (v_data->>'has_time')::boolean;
            v_due := v_data->>'due_at';
            v_end := v_data->>'end_at';
            IF v_due IS NULL AND v_end IS NOT NULL THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'An end date requires a start date';
            END IF;
            BEGIN
                IF v_has_time THEN
                    IF (v_due IS NOT NULL AND v_due !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.[0-9]{1,7})?(Z|[+-]([01][0-9]|2[0-3]):[0-5][0-9])$')
                       OR (v_end IS NOT NULL AND v_end !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.[0-9]{1,7})?(Z|[+-]([01][0-9]|2[0-3]):[0-5][0-9])$') THEN
                        RAISE invalid_datetime_format;
                    END IF;
                    IF v_due IS NOT NULL THEN PERFORM v_due::timestamptz; END IF;
                    IF v_end IS NOT NULL AND v_end::timestamptz < v_due::timestamptz THEN
                        RAISE invalid_datetime_format;
                    END IF;
                ELSE
                    IF (v_due IS NOT NULL AND (v_due !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR to_char(v_due::date, 'YYYY-MM-DD') <> v_due))
                       OR (v_end IS NOT NULL AND (v_end !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR to_char(v_end::date, 'YYYY-MM-DD') <> v_end)) THEN
                        RAISE invalid_datetime_format;
                    END IF;
                    IF v_end IS NOT NULL AND v_end::date < v_due::date THEN
                        RAISE invalid_datetime_format;
                    END IF;
                END IF;
            EXCEPTION WHEN invalid_datetime_format OR datetime_field_overflow OR invalid_time_zone_displacement_value THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Invalid calendar dates or timezone';
            END;
        END IF;

        -- Canonical JSONB representation makes property order irrelevant. Device
        -- ID is intentionally excluded: a persisted retry may move to a new device.
        v_request_hash := extensions.digest(v_change::text, 'sha256');
        SELECT * INTO v_receipt FROM daynote_private.receipts
          WHERE account_id = v_account.id AND mutation_id = v_change->>'mutation_id';
        IF FOUND THEN
            IF v_receipt.request_hash IS DISTINCT FROM v_request_hash THEN
                RAISE EXCEPTION USING ERRCODE = '22023', MESSAGE = 'Mutation identifier was reused with different content';
            END IF;
            v_results := v_results || jsonb_build_array(v_receipt.result);
            CONTINUE;
        END IF;

        SELECT * INTO v_event FROM daynote_private.events
          WHERE account_id = v_account.id AND id = v_change->>'id';
        v_current := CASE WHEN FOUND THEN v_event.version ELSE 0 END;
        IF v_current <> v_base THEN
            v_result := jsonb_build_object('id',v_change->>'id','mutation_id',v_change->>'mutation_id',
                                           'status','conflict','version',v_current);
        ELSE
            IF v_current = 0 AND v_count >= 10000 THEN
                RAISE EXCEPTION USING ERRCODE = '54000', MESSAGE = 'Calendar exceeds preview record limit';
            END IF;
            IF v_current >= 2147483646 THEN
                RAISE EXCEPTION USING ERRCODE = '54000', MESSAGE = 'Calendar version limit reached';
            END IF;
            v_new_version := v_current + 1;
            v_when := clock_timestamp();
            -- Keep original fields when deleting an existing event. The marker
            -- is what propagates a deletion; it must never be inferred from absence.
            IF v_deleted AND v_data = '{}'::jsonb AND v_current > 0 THEN
                v_data := v_event.data;
            END IF;
            INSERT INTO daynote_private.events(account_id,id,version,deleted,data,updated_at)
              VALUES(v_account.id,v_change->>'id',v_new_version,v_deleted,v_data,v_when)
              ON CONFLICT (account_id,id) DO UPDATE SET version=EXCLUDED.version,
                deleted=EXCLUDED.deleted,data=EXCLUDED.data,updated_at=EXCLUDED.updated_at;
            IF v_current = 0 THEN v_count := v_count + 1; END IF;
            v_result := jsonb_build_object('id',v_change->>'id','mutation_id',v_change->>'mutation_id',
                                           'status','applied','version',v_new_version);
        END IF;
        INSERT INTO daynote_private.receipts(account_id,mutation_id,request_hash,result)
          VALUES(v_account.id,v_change->>'mutation_id',v_request_hash,v_result);
        v_results := v_results || jsonb_build_array(v_result);
    END LOOP;

    SELECT coalesce(jsonb_agg(jsonb_build_object('id',id,'version',version,'deleted',deleted,
                                               'data',data,'updated_at',updated_at) ORDER BY id),'[]'::jsonb)
      INTO v_items FROM daynote_private.events WHERE account_id = v_account.id;
    RETURN jsonb_build_object('account_id',v_account.id,'account_label',v_account.label,
                             'server_time',clock_timestamp(),'results',v_results,'items',v_items);
END;
$function$;

REVOKE ALL ON FUNCTION public.daynote_sync_v1(text,text,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.daynote_sync_v1(text,text,jsonb) TO anon, authenticated;
COMMENT ON FUNCTION public.daynote_sync_v1(text,text,jsonb) IS
  'Daynote private preview: key-scoped, transactional, versioned sync. Never log request bodies.';
NOTIFY pgrst, 'reload schema';
COMMIT;
