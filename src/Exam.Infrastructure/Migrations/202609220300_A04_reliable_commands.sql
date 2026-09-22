CREATE SCHEMA IF NOT EXISTS exam;

CREATE TABLE IF NOT EXISTS exam.command_events
(
    event_id                       text PRIMARY KEY,
    create_request_id              text NOT NULL UNIQUE,
    exam_id                        text NOT NULL,
    participation_id               text NULL,
    entry_version                  bigint NOT NULL CHECK (entry_version >= 0),
    event_type                     text NOT NULL CHECK (event_type IN
                                      ('Start', 'End', 'ScheduleChanged', 'PaperUpdated',
                                       'DeviceDecided', 'CollectionCompleted')),
    state_version                  bigint NOT NULL CHECK (state_version >= 0),
    target_device_id               text NULL,
    target_authorization_version   bigint NULL,
    resource_version               bigint NULL,
    protected_unlock_reference     text NULL,
    metadata                       jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at                     timestamptz NOT NULL,
    next_delivery_at               timestamptz NOT NULL,
    last_delivery_attempt_at       timestamptz NULL,
    delivery_attempts              integer NOT NULL DEFAULT 0 CHECK (delivery_attempts >= 0),
    acknowledgement_request_id     text NULL,
    acknowledgement_result         text NULL,
    acknowledgement_paper_version  bigint NULL,
    acknowledgement_failure_reason text NULL,
    acknowledged_at                timestamptz NULL,
    CONSTRAINT ck_command_event_target CHECK
    (
        (event_type = 'CollectionCompleted') OR
        (participation_id IS NOT NULL AND
         target_device_id IS NOT NULL AND
         target_authorization_version IS NOT NULL)
    ),
    CONSTRAINT ck_command_event_unlock_reference CHECK
    (
        (event_type = 'Start' AND protected_unlock_reference IS NOT NULL) OR
        (event_type <> 'Start' AND protected_unlock_reference IS NULL)
    ),
    CONSTRAINT ck_command_event_ack_result CHECK
    (
        acknowledgement_result IS NULL OR
        acknowledgement_result IN
            ('Applied', 'Received', 'ResourceUpdateFailed', 'RejectedAsStale')
    ),
    CONSTRAINT ck_command_event_ack_shape CHECK
    (
        (acknowledgement_result IS NULL AND
         acknowledgement_request_id IS NULL AND
         acknowledgement_paper_version IS NULL AND
         acknowledgement_failure_reason IS NULL AND
         acknowledged_at IS NULL) OR
        (acknowledgement_result IS NOT NULL AND
         acknowledgement_request_id IS NOT NULL AND
         acknowledged_at IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS ix_command_events_due
    ON exam.command_events (next_delivery_at, created_at, event_id)
    WHERE acknowledgement_result IS NULL OR
          acknowledgement_result NOT IN ('Applied', 'RejectedAsStale');

CREATE INDEX IF NOT EXISTS ix_command_events_target
    ON exam.command_events
       (participation_id, entry_version, target_device_id, target_authorization_version);

CREATE TABLE IF NOT EXISTS exam.command_acknowledgement_requests
(
    lookup_request_id   text PRIMARY KEY,
    event_id            text NOT NULL REFERENCES exam.command_events(event_id) ON DELETE RESTRICT,
    result_request_id   text NOT NULL,
    processing_result   text NOT NULL CHECK
                        (processing_result IN
                            ('Applied', 'Received', 'ResourceUpdateFailed', 'RejectedAsStale')),
    actual_paper_version bigint NULL,
    failure_reason      text NULL,
    acknowledged_at     timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_command_ack_requests_event
    ON exam.command_acknowledgement_requests (event_id);

COMMENT ON COLUMN exam.command_events.protected_unlock_reference IS
    'Reference to protected key material only. Plaintext exam keys must never be stored here.';
