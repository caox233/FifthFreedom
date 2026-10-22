-- A private-room N-1 regroup temporarily leaves each survivor's preserved
-- personal lobby while an accepted automatic restore is still in flight.
-- That transition is not an explicit Leave Party action and must not erase
-- the authoritative Party before the successor lobby is ready.
DROP TRIGGER IF EXISTS scbl_party_member_leave_lobby;

CREATE TRIGGER scbl_party_member_leave_lobby
AFTER DELETE ON participants
WHEN EXISTS (
    SELECT 1
    FROM game_sessions AS lobby
    WHERE lobby.id = OLD.game_id
      AND (';' || replace(lobby.attributes, ' ', '') || ';') LIKE '%;113=>1;%'
)
AND NOT EXISTS (
    SELECT 1
    FROM participants AS current_participant
    INNER JOIN game_sessions AS current_session
        ON current_session.id = current_participant.game_id
    WHERE current_participant.user_id = OLD.user_id
      AND current_session.destroyed_at IS NULL
      AND (';' || replace(current_session.attributes, ' ', '') || ';') LIKE '%;113=>0;%'
)
AND NOT EXISTS (
    SELECT 1
    FROM invites AS restore
    INNER JOIN party_members AS membership
        ON membership.user_id = OLD.user_id
       AND membership.party_id = restore.party_id
    WHERE restore.receiver = OLD.user_id
      AND restore.kind = 4
      AND restore.accepted_at IS NOT NULL
      AND restore.consumed_at IS NULL
      AND restore.expires_at > CURRENT_TIMESTAMP
)
BEGIN
    DELETE FROM parties WHERE leader_id = OLD.user_id;
    DELETE FROM party_members WHERE user_id = OLD.user_id;
END;
