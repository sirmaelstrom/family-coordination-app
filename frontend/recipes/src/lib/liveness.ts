// ─────────────────────────────────────────────────────────────────────────
// Liveness — lightweight collaboration refresh for the recipes LIST view.
//
// A recipe added/edited/deleted by another household member should appear
// within ~20s while the tab is VISIBLE, and immediately when the tab regains
// focus. We do this with a plain interval + `visibilitychange`, NOT Blazor's
// DataNotifier / PresenceService / PollingService (those are SignalR-circuit-
// bound — the exact failure mode the strangler removes).
//
// The poll PAUSES while the tab is hidden: no interval ticks fire when
// `document.hidden` is true, and on re-show we refetch once immediately, then
// resume the interval. This keeps background tabs quiet.
//
// Wired ONLY in ListApp — the edit form is single-user (autosave-drafts is its
// persistence path); a poll there would clobber in-progress edits.
// ─────────────────────────────────────────────────────────────────────────

const POLL_INTERVAL_MS = 20_000;

export interface LivenessHandle {
  /** Tear down the interval + visibility listener. Call on unmount. */
  stop(): void;
}

/**
 * Start polling. `refresh` is the list reload (ListApp's reconcile). It is only
 * invoked while the document is visible. Returns a handle whose `stop()` removes
 * the interval + listener.
 */
export function startLiveness(refresh: () => void): LivenessHandle {
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
    timer = setInterval(tick, POLL_INTERVAL_MS);
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
