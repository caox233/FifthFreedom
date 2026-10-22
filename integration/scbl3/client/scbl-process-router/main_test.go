//go:build windows

package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestSessionGuardRetainsAuthorisedLiveGameAfterLauncherCrash(t *testing.T) {
	path, alive := prepareGuardFixture(t, []uint32{200})
	g, err := newLauncherSessionGuard(path, "session-a", 100, time.Second, 40*time.Millisecond)
	if err != nil {
		t.Fatal(err)
	}
	alive[100] = false
	if err := g.refresh(); err != nil {
		t.Fatalf("live authorised game must survive launcher loss: %v", err)
	}
	if !g.TargetPIDs()[200] {
		t.Fatal("authorised live game PID was discarded")
	}
}

func TestSessionGuardExitsOnlyAfterContinuousLauncherAndGameIdleGrace(t *testing.T) {
	path, alive := prepareGuardFixture(t, []uint32{200})
	g, err := newLauncherSessionGuard(path, "session-a", 100, time.Second, 35*time.Millisecond)
	if err != nil {
		t.Fatal(err)
	}
	alive[100] = false
	alive[200] = false
	if err := g.refresh(); err != nil {
		t.Fatalf("first idle sample must start grace, not exit: %v", err)
	}
	time.Sleep(45 * time.Millisecond)
	if err := g.refresh(); err == nil {
		t.Fatal("guard did not expire after continuous launcher/game absence")
	}
}

func TestValidHeartbeatCancelsIdleCountdown(t *testing.T) {
	path, alive := prepareGuardFixture(t, nil)
	g, err := newLauncherSessionGuard(path, "session-a", 100, time.Second, 45*time.Millisecond)
	if err != nil {
		t.Fatal(err)
	}
	alive[100] = false
	if err := g.refresh(); err != nil {
		t.Fatal(err)
	}
	time.Sleep(30 * time.Millisecond)
	alive[100] = true
	writeGuardState(t, path, "session-a", 100, nil)
	if err := g.refresh(); err != nil {
		t.Fatalf("fresh valid heartbeat must cancel idle grace: %v", err)
	}
	alive[100] = false
	if err := g.refresh(); err != nil {
		t.Fatalf("new idle grace must start after the last valid heartbeat: %v", err)
	}
}

func TestFreshPredecessorHandoffCancelsIdleCountdown(t *testing.T) {
	path, alive := prepareGuardFixture(t, nil)
	g, err := newLauncherSessionGuard(path, "session-a", 100, time.Second, 45*time.Millisecond)
	if err != nil {
		t.Fatal(err)
	}
	alive[100] = false
	if err := g.refresh(); err != nil {
		t.Fatal(err)
	}
	time.Sleep(30 * time.Millisecond)
	alive[300] = true
	state := launcherSessionState{
		SessionID:          "session-b",
		LauncherPID:        300,
		PreviousSessionID:  "session-a",
		PreviousLauncherID: 100,
		UpdatedAtUnixMs:    time.Now().UnixMilli(),
		AllowEmptyGamePIDs: true,
	}
	writeState(t, path, state)
	if err := g.refresh(); err != nil {
		t.Fatalf("valid predecessor handoff must cancel idle grace: %v", err)
	}
	if g.expectedLauncher != 300 || g.expectedSession != "session-b" {
		t.Fatal("guard did not adopt the new Launcher identity")
	}
}

func TestForgedSessionDoesNotResetIdleCountdown(t *testing.T) {
	path, alive := prepareGuardFixture(t, nil)
	g, err := newLauncherSessionGuard(path, "session-a", 100, time.Second, 35*time.Millisecond)
	if err != nil {
		t.Fatal(err)
	}
	alive[100] = false
	if err := g.refresh(); err != nil {
		t.Fatal(err)
	}
	writeGuardState(t, path, "forged-session", 999, nil)
	alive[999] = true
	time.Sleep(45 * time.Millisecond)
	if err := g.refresh(); err == nil {
		t.Fatal("mismatched session heartbeat reset the trusted idle countdown")
	}
}

func TestStaleHeartbeatDoesNotResetIdleCountdown(t *testing.T) {
	path, _ := prepareGuardFixture(t, nil)
	g, err := newLauncherSessionGuard(path, "session-a", 100, time.Second, 35*time.Millisecond)
	if err != nil {
		t.Fatal(err)
	}
	state := launcherSessionState{
		SessionID:          "session-a",
		LauncherPID:        100,
		UpdatedAtUnixMs:    time.Now().Add(-2 * time.Second).UnixMilli(),
		AllowEmptyGamePIDs: true,
	}
	writeState(t, path, state)
	if err := g.refresh(); err != nil {
		t.Fatal(err)
	}
	time.Sleep(45 * time.Millisecond)
	if err := g.refresh(); err == nil {
		t.Fatal("stale heartbeat reset the trusted idle countdown")
	}
}

func prepareGuardFixture(t *testing.T, gamePIDs []uint32) (string, map[uint32]bool) {
	t.Helper()
	alive := map[uint32]bool{100: true}
	for _, pid := range gamePIDs {
		alive[pid] = true
	}
	previous := processAlive
	processAlive = func(pid uint32) bool { return alive[pid] }
	t.Cleanup(func() { processAlive = previous })
	path := filepath.Join(t.TempDir(), "route-guard-session.json")
	writeGuardState(t, path, "session-a", 100, gamePIDs)
	return path, alive
}

func writeGuardState(t *testing.T, path, sessionID string, launcherPID uint32, gamePIDs []uint32) {
	t.Helper()
	state := launcherSessionState{
		SessionID:          sessionID,
		LauncherPID:        launcherPID,
		UpdatedAtUnixMs:    time.Now().UnixMilli(),
		GamePIDs:           gamePIDs,
		AllowEmptyGamePIDs: true,
	}
	writeState(t, path, state)
}

func writeState(t *testing.T, path string, state launcherSessionState) {
	t.Helper()
	data, err := json.Marshal(state)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, data, 0600); err != nil {
		t.Fatal(err)
	}
}
