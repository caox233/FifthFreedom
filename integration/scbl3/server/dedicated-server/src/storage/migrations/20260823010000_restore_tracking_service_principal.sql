-- `Tracking` is a fixed Quazal service principal used by the retail game.
-- It is not a development player account.  A previous cleanup migration
-- removed the untouched seed row, which made TicketGrantingProtocol.Login
-- fail after the player's normal LoginEx had already succeeded.

INSERT INTO users (id, username, password, password_hash, ubi_id)
SELECT 105, 'Tracking', 'JaDe!', NULL, NULL
WHERE NOT EXISTS (
    SELECT 1 FROM users
    WHERE id = 105 OR username = 'Tracking'
);

-- Fail closed if either stable identity is occupied by different data.  The
-- temporary CHECK guard aborts the migration without overwriting player data.
CREATE TEMP TABLE scbl_tracking_service_principal_guard (
    valid INTEGER NOT NULL CHECK (valid = 1)
);

INSERT INTO scbl_tracking_service_principal_guard (valid)
SELECT CASE WHEN EXISTS (
    SELECT 1
    FROM users
    WHERE id = 105
      AND username = 'Tracking'
      AND password = 'JaDe!'
      AND password_hash IS NULL
      AND ubi_id IS NULL
) THEN 1 ELSE 0 END;

DROP TABLE scbl_tracking_service_principal_guard;
