-- Old initial migrations inserted development-only users so the original
-- prototype could be exercised without registration. Keep those historical
-- migration bytes immutable for databases that already recorded their
-- checksums, and remove only rows that still have the exact untouched demo
-- identity and have never authenticated.

DELETE FROM game_sessions
WHERE id = 1
  AND type_id = 1
  AND creator_id = 1000
  AND attributes IS NULL
  AND destroyed_at IS NULL
  AND EXISTS (
      SELECT 1 FROM users
      WHERE id = 1000
        AND username = 'Foo'
        AND password IS NULL
        AND password_hash IS NULL
        AND ubi_id = 'MYID'
        AND last_login IS NULL
  )
  AND NOT EXISTS (
      SELECT 1 FROM participants
      WHERE game_id = 1 AND user_id <> 1000
  );

DELETE FROM station_urls
WHERE user_id = 1000
  AND url = 'prudp:/address=127.0.0.1;port=19999;hdrType=00000000;RVCID=1234;type=2'
  AND EXISTS (
      SELECT 1 FROM users
      WHERE id = 1000
        AND username = 'Foo'
        AND password IS NULL
        AND password_hash IS NULL
        AND ubi_id = 'MYID'
        AND last_login IS NULL
  );

DELETE FROM users
WHERE id = 1000
  AND username = 'Foo'
  AND password IS NULL
  AND password_hash IS NULL
  AND ubi_id = 'MYID'
  AND last_login IS NULL
  AND NOT EXISTS (SELECT 1 FROM game_sessions WHERE creator_id = 1000)
  AND NOT EXISTS (SELECT 1 FROM station_urls WHERE user_id = 1000)
  AND NOT EXISTS (SELECT 1 FROM participants WHERE user_id = 1000)
  AND NOT EXISTS (SELECT 1 FROM user_sessions WHERE user_id = 1000)
  AND NOT EXISTS (SELECT 1 FROM invites WHERE sender = 1000 OR receiver = 1000);

DELETE FROM users
WHERE id = 105
  AND username = 'Tracking'
  AND password = 'JaDe!'
  AND password_hash IS NULL
  AND ubi_id IS NULL
  AND last_login IS NULL
  AND NOT EXISTS (SELECT 1 FROM user_sessions WHERE user_id = 105)
  AND NOT EXISTS (SELECT 1 FROM invites WHERE sender = 105 OR receiver = 105);

DELETE FROM users
WHERE id = 1001
  AND username = 'AAAABBBB'
  AND password IS NULL
  AND password_hash = '$argon2id$v=19$m=19456,t=2,p=1$ak/934K3+OsQ71Dogbr+Iw$Fy3aLbg2bQFXrnucys2gsBqiy2Jgv9QMBWWiPzS7VTk'
  AND ubi_id = 'ABCD'
  AND last_login IS NULL
  AND NOT EXISTS (SELECT 1 FROM user_sessions WHERE user_id = 1001)
  AND NOT EXISTS (SELECT 1 FROM invites WHERE sender = 1001 OR receiver = 1001);

DELETE FROM users
WHERE id = 1002
  AND username = 'sam_the_fisher'
  AND password IS NULL
  AND password_hash = '$argon2id$v=19$m=16,t=2,p=1$c1hvR0JwNTMyaTQ2Nk9uMQ$JOefJqEVFQhLM580DBnqXA'
  AND ubi_id = 'SAM'
  AND last_login IS NULL
  AND NOT EXISTS (SELECT 1 FROM user_sessions WHERE user_id = 1002)
  AND NOT EXISTS (SELECT 1 FROM invites WHERE sender = 1002 OR receiver = 1002);
