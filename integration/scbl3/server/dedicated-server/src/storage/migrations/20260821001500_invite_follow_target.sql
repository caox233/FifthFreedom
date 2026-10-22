-- A manual invitation issued while a Party is already in a match is a
-- two-stage transition: join the native Party lobby first, then follow that
-- exact Party generation into the exact match selected at invitation time.
ALTER TABLE invites ADD COLUMN follow_session_type INTEGER;
ALTER TABLE invites ADD COLUMN follow_session_id INTEGER;
