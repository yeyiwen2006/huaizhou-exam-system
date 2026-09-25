-- Prerequisites: A05 submissions/outbox and D04 grading results migrations.
-- Additive only: no frozen answer, receipt, existing task or result is rewritten.
-- New claims persist their own expiry: another consumer cannot shorten an active lease.
-- Legacy claims without expiry use the caller's lease cutoff until reclaimed once.
-- During rollout pause all old consumers, apply migrations, then start the new consumer version.
ALTER TABLE exam.grading_outbox
    ADD COLUMN IF NOT EXISTS lease_expires_at timestamptz NULL;

CREATE INDEX IF NOT EXISTS ix_grading_outbox_fair_claim
    ON exam.grading_outbox (attempt_count, created_at, submission_id, grading_version)
    WHERE completed_at IS NULL;

-- Recovery: pause consumers. Prefer retaining this nullable column and index on code rollback.
-- A full schema rollback may drop only this index/column after checking no active consumer uses them.
-- schema_migrations records '202609252200_A01_grading_leases' WITHOUT '.sql'.
-- Reapplication uses PostgresMigrationRunner and does not delete/recreate A05 source data.
