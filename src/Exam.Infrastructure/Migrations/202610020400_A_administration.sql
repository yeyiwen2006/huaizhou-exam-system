-- A-side roster, room and presence state. No sample users, credentials or examination content.
CREATE TABLE exam.administration_requests (
 request_id text PRIMARY KEY, operation text NOT NULL, scope text NOT NULL,
 payload_hash text NOT NULL CHECK(payload_hash ~ '^[0-9A-F]{64}$'),
 response_json jsonb NOT NULL, actor_id text NOT NULL, created_at timestamptz NOT NULL);
CREATE TABLE exam.roster_heads (
 exam_id text PRIMARY KEY REFERENCES exam.exam_entry_access(exam_id),
 version bigint NOT NULL CHECK(version>=0));
INSERT INTO exam.roster_heads(exam_id,version)
 SELECT e.exam_id, CASE WHEN EXISTS(SELECT 1 FROM exam.candidate_roster_entries r WHERE r.exam_id=e.exam_id) THEN 1 ELSE 0 END
 FROM exam.exam_entry_access e;
CREATE TABLE exam.room_groups (
 group_id text PRIMARY KEY, name text NOT NULL, name_key text NOT NULL UNIQUE,
 version bigint NOT NULL CHECK(version>0), created_at timestamptz NOT NULL,
 updated_at timestamptz NOT NULL, updated_by text NOT NULL,
 CHECK(length(btrim(name)) BETWEEN 1 AND 80));
-- Preserve earlier room identifiers rather than silently ungrouping previously activated devices.
INSERT INTO exam.room_groups(group_id,name,name_key,version,created_at,updated_at,updated_by)
 SELECT room_group_id, '迁移试场 '||row_number() OVER(ORDER BY room_group_id),
        '迁移试场 '||row_number() OVER(ORDER BY room_group_id),1,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP,'migration'
 FROM (SELECT DISTINCT room_group_id FROM exam.activation_devices WHERE room_group_id IS NOT NULL) old_groups;
ALTER TABLE exam.activation_devices ADD CONSTRAINT fk_activation_device_room
 FOREIGN KEY(room_group_id) REFERENCES exam.room_groups(group_id) ON DELETE RESTRICT;
CREATE INDEX ix_activation_devices_room ON exam.activation_devices(room_group_id,client_number);
CREATE TABLE exam.device_room_history (
 request_id text NOT NULL, device_id text NOT NULL REFERENCES exam.activation_devices(device_id),
 client_number text NOT NULL, from_group_id text NULL, from_group_name text NOT NULL,
 to_group_id text NULL, to_group_name text NOT NULL, actor_id text NOT NULL,
 reason text NOT NULL, changed_at timestamptz NOT NULL, PRIMARY KEY(request_id,device_id));
CREATE INDEX ix_device_room_history_device ON exam.device_room_history(device_id,changed_at DESC);
CREATE TABLE exam.device_presence (
 device_id text PRIMARY KEY REFERENCES exam.activation_devices(device_id),
 connection_id text NOT NULL, generation bigint NOT NULL CHECK(generation>0),
 sequence bigint NOT NULL DEFAULT 0 CHECK(sequence>=0), payload_hash text NULL, session_id text NULL,
 exam_id text NULL, page text NOT NULL DEFAULT '登录', current_question_id text NULL,
 installed_paper_version bigint NOT NULL DEFAULT 0 CHECK(installed_paper_version>=0),
 download_state text NOT NULL DEFAULT '未下载', recovering boolean NOT NULL DEFAULT false,
 last_seen_at timestamptz NOT NULL, disconnected_at timestamptz NULL,
 hub_connection_id text NULL, updated_at timestamptz NOT NULL);
CREATE INDEX ix_device_presence_last_seen ON exam.device_presence(last_seen_at);
