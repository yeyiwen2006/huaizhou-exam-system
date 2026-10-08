-- Preserve existing identities, exam records and legacy numbers. New logins use
-- YYYYMMDDXXXX; legacy numbers are replaced when that device next logs in.
ALTER TABLE exam.activation_daily_sequences
    DROP CONSTRAINT ck_activation_daily_sequences_range;
ALTER TABLE exam.activation_daily_sequences
    ADD CONSTRAINT ck_activation_daily_sequences_range CHECK (last_sequence BETWEEN 0 AND 9999);

ALTER TABLE exam.activation_devices DROP CONSTRAINT ck_activation_devices_client_number;
ALTER TABLE exam.activation_devices ADD CONSTRAINT ck_activation_devices_client_number
    CHECK (client_number ~ '^[0-9]{11,12}$');
ALTER TABLE exam.device_activation_requests DROP CONSTRAINT ck_device_activation_requests_client_number;
ALTER TABLE exam.device_activation_requests ADD CONSTRAINT ck_device_activation_requests_client_number
    CHECK (client_number IS NULL OR client_number ~ '^[0-9]{11,12}$');

COMMENT ON TABLE exam.activation_daily_sequences IS
    'One locked row per server-local login date; values stop at 9999 and survive restarts.';
