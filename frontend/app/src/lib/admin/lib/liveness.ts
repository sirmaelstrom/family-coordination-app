// ─────────────────────────────────────────────────────────────────────────
// Liveness — lightweight collaboration refresh for the admin island (the one
// settings cluster that polls, review R-C9). Per-view cadence: households 30s,
// feedback 15s (parity with the old Blazor timers).
//
// A change made by another admin/household member should appear within the
// interval while the tab is VISIBLE, and immediately when the tab regains focus.
// We do this with a plain interval + `visibilitychange`, NOT Blazor's
// DataNotifier / PresenceService / PollingService (those are SignalR-circuit-bound).
//
// The poll PAUSES while the tab is hidden (visible-only, R-C9): no interval ticks
// fire when `document.hidden` is true, and on re-show we refetch once immediately,
// then resume the interval. This keeps background tabs quiet (a tiny, strictly-
// better behaviour change vs the old always-on timer). The caller's loader carries
// the loadSeq guard, so a stale poll response can never clobber a newer one.
// ─────────────────────────────────────────────────────────────────────────

const DEFAULT_INTERVAL_MS = 20_000;

export interface LivenessHandle {
  /** Tear down the interval + visibility listener. Call on unmount. */
  stop(): void;
}

/**
 * Start polling. `refresh` is the view reload (App.svelte's loader). It is
 * only invoked while the document is visible. `intervalMs` sets the cadence
 * (households 30s, feedback 15s). Returns a handle whose `stop()` removes the
 * interval + listener.
 */
export function startLiveness(refresh: () => void, intervalMs: number = DEFAULT_INTERVAL_MS): LivenessHandle {
  let timer: ReturnType<typeof setInterval> | null = null;

  function tick() {
    // Guard: never poll while hidden (the interval is cleared on hide, but this
    // is belt-and-suspenders in case a tick is queued at the moment of hiding).
    if (document.visibilityState === 'visible') {
      refresh();
    }
  }

  function startInterval() {
    if (timer != null) return;
    timer = setInterval(tick, intervalMs);
  }

  function stopInterval() {
    if (timer != null) {
      clearInterval(timer);
      timer = null;
    }
  }

  function handleVisibility() {
    if (document.visibilityState === 'visible') {
      // Re-show: refetch immediately, then resume the interval.
      refresh();
      startInterval();
    } else {
      // Hidden: pause polling entirely.
      stopInterval();
    }
  }

  document.addEventListener('visibilitychange', handleVisibility);

  // Begin polling only if we start visible.
  if (document.visibilityState === 'visible') {
    startInterval();
  }

  return {
    stop() {
      stopInterval();
      document.removeEventListener('visibilitychange', handleVisibility);
    },
  };
}
