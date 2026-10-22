-- Distinguish a live one-player private room from the old host record that is
-- temporarily preserved while retail clients finish native host migration.
ALTER TABLE game_sessions ADD COLUMN host_leave_pending INTEGER NOT NULL DEFAULT 0 CHECK (host_leave_pending IN (0, 1));
