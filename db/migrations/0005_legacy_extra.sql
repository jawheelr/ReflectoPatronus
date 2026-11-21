-- 0005: lossless fallback for the legacy system import.
-- Columns the model deliberately dropped are verified by the importer. If a dropped column holds a real (non-empty,
-- non-constant) value on some row, that value is kept here as JSON instead of being lost. NULL for almost every row,
-- so it costs ~nothing. Also records broken legacy links (e.g. a customer number that no longer exists).
ALTER TABLE customer      ADD COLUMN legacy_extra jsonb;
ALTER TABLE part          ADD COLUMN legacy_extra jsonb;
ALTER TABLE document      ADD COLUMN legacy_extra jsonb;
ALTER TABLE document_line ADD COLUMN legacy_extra jsonb;
ALTER TABLE payment       ADD COLUMN legacy_extra jsonb;
ALTER TABLE ar_entry      ADD COLUMN legacy_extra jsonb;
