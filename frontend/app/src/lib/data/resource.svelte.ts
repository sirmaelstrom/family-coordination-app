// ─────────────────────────────────────────────────────────────────────────
// Resource<T> — one owner for WHEN a load or a write may land (quest 41337b6f;
// spec data/outputs/workshops/fca-resource-41337b6f/spec.md).
//
// Stores keep their own data ($state fields, their public `loading`/`error`); a Resource owns only
// the ordering, so a store says WHAT to fetch, patch and commit, and this module says WHEN:
//
//   • load()   — a monotonic id drops a superseded response (its error too).
//   • writes   — mutate()/run() open a write window. While ANY write is pending, load() dispatches
//                nothing (it is 'deferred' and owed); a load already in flight when a write starts is
//                retired (and owed). So no GET whose request overlaps a pending write can commit, in
//                either response order — the fca#103 residual (a stale GET answered BEFORE the write's
//                response) is closed by construction, not per call site.
//   • settle   — when the last write settles, ONE load starts if one is owed or a reconcile is due, so
//                the view converges on server truth without waiting for the next liveness tick. The
//                write resolves without awaiting it (a stalled GET must not hold its outcome); settled()
//                waits for it — or for the newer load that superseded it — to finish as current.
//   • mutate() — optimistic patch (synchronous) → call → commit, or undo + the 409 / other-4xx /
//                5xx-network three-way branch through the ApiError getters (never a raw status).
//
// Local callback failures (optimistic / undo / commit throwing) are programming errors, kept apart
// from server failures: they never take the server-failure branch, the window still settles, a
// reconcile is scheduled, and the error is rethrown. A commit that throws after a SUCCESSFUL call is
// never undone — the server has the write.
// ─────────────────────────────────────────────────────────────────────────

import { ApiError } from '$lib/api/client';
import { showToast } from '$lib/shared/toast-store.svelte';

export type LoadOutcome = 'committed' | 'superseded' | 'deferred' | 'failed';
export type MutationOutcome = 'ok' | 'conflict' | 'rejected' | 'failed' | 'skipped';
/**
 * When a write reconciles (reloads) at settle:
 *   'on-reject' — after a 409 or another 4xx (the mutate default);
 *   'on-error'  — after ANY failure (409, 4xx, 5xx, network), never after a success — for stores whose
 *                 rollback is "refetch the truth" whatever went wrong (chores subtasks + remove);
 *   'always'    — after every outcome, success included (the run default: write-then-reload stores);
 *   'never'     — no reconcile (a load the write retired is still owed).
 */
export type ReloadPolicy = 'on-reject' | 'on-error' | 'always' | 'never';
type Key = string | number;

export interface ResourceOptions<T> {
  /** Reads store state at call time; may return request context with the DTO (e.g. `{ week, board }`). */
  fetch: () => Promise<T>;
  /** Apply a CURRENT load's result. */
  commit: (value: T) => void;
  /** A load was dispatched (stores map their own `loading` meaning from these hooks). */
  onLoadStart?: () => void;
  /** Only for a current load; a superseded load's error is dropped. */
  onLoadError?: (e: unknown) => void;
  /** No current load remains: it committed, failed, or was retired by a write. */
  onLoadIdle?: () => void;
}

export interface MutationSpec<R> {
  /** Double-submit guard: a mutate on a pending key returns 'skipped' and runs nothing. */
  key?: Key;
  /** Runs synchronously, before any await. Returns its own undo (scope-guarded, item-scoped). */
  optimistic?: () => (() => void) | void;
  call: () => Promise<R>;
  /** Apply the authoritative response (inside the write window). */
  commit?: (result: R) => void;
  /** Reconcile policy. Default 'on-reject': reload after a 409 or another 4xx. */
  reload?: ReloadPolicy;
  /** A toast per failure branch (conflict / rejected → info, failed → error). */
  messages?: { conflict?: string; rejected?: string; failed?: string };
}

export interface RunSpec<R> {
  call: () => Promise<R>;
  /** Applied inside the write window, before settle. */
  commit?: (result: R) => void;
  /** Default 'always' (the write-then-reload stores). */
  reload?: ReloadPolicy;
}

/** The three-way branch, through the ApiError getters. A 5xx or a network error is 'failed'. */
export function classifyFailure(e: unknown): 'conflict' | 'rejected' | 'failed' {
  if (e instanceof ApiError && e.isConflict) return 'conflict';
  if (e instanceof ApiError && e.isClientRejection) return 'rejected';
  return 'failed';
}

function reloadsAfter(policy: ReloadPolicy, outcome: MutationOutcome): boolean {
  if (policy === 'always') return true;
  if (policy === 'never') return false;
  if (policy === 'on-error') return outcome !== 'ok' && outcome !== 'skipped';
  return outcome === 'conflict' || outcome === 'rejected';
}

export class Resource<T> {
  /** A CURRENT load is in flight (cleared when a write retires it). */
  loading = $state(false);
  /** A load has committed at least once. */
  hasLoaded = $state(false);
  /** Pending write keys — replaced (copy-on-write) on every change so reads stay reactive. */
  #pendingKeys = $state<ReadonlySet<Key>>(new Set());

  #seq = 0;
  /** The id of the load in flight that may still commit, or null. */
  #current: number | null = null;
  #writes = 0;
  /** A load was refused (deferred) or retired while writes were pending — run one at settle. */
  #owed = false;
  /** A write asked for a reconcile — run one at settle. */
  #reconcileDue = false;
  /**
   * What `settled()` waits for: opened when a settle starts its owed/reconcile load, resolved when a
   * load finishes as CURRENT after it — the settle load itself (once its error, if any, is reported),
   * or the newer load that superseded it (council r5: a superseded settle load must not resolve it
   * early, and a stalled superseded one must not keep it open once a successor has landed).
   */
  #settleWaiter: { promise: Promise<void>; resolve: () => void } | null = null;
  /** The id of the load the last settle started (its completion is reported through its own chain). */
  #settleLoadId = 0;
  /**
   * Keys of conflicted/rejected writes, still pending until a CURRENT load has finished (committed or
   * failed) — not until a particular promise does. The reconcile load the settle starts may be
   * superseded by a newer load (an explicit load, or another write's owed load); the hold then passes
   * to whichever load finishes as current, so a superseded reconcile never releases a key before
   * fresh state has landed, and a stalled superseded one never keeps it locked after (council r3/r4).
   * Any load that finishes as current started after the hold: a load in flight when the write began
   * was retired by it, and none can start while a write is pending.
   */
  #heldKeys = new Set<Key>();
  readonly #options: ResourceOptions<T>;

  constructor(options: ResourceOptions<T>) {
    this.#options = options;
  }

  get writesPending(): boolean {
    return this.#writes > 0;
  }

  /**
   * Resolves once a load has finished as current — committed, failed, or its start hook threw — after
   * the last settle started its owed/reconcile load: that load, or a newer one that superseded it. So
   * it marks "the reconcile attempt is over", not "fresh data is on screen" (a failure resolves it too).
   * Immediately when no settle load is outstanding. Writes that keep arriving keep moving it later.
   * Writes don't wait for it (a stalled GET must not withhold a write's outcome or its toast); callers
   * and tests that need the reconciled view do.
   */
  settled(): Promise<void> {
    return this.#settleWaiter?.promise ?? Promise.resolve();
  }

  isPending(key: Key): boolean {
    return this.#pendingKeys.has(key);
  }

  /** The pending keys. A NEW set on every change — that replacement is what makes reads reactive. */
  get pendingKeys(): ReadonlySet<Key> {
    return this.#pendingKeys;
  }

  /** Load now — or, while a write is pending, dispatch nothing and owe one load at settle. */
  async load(): Promise<LoadOutcome> {
    if (this.#writes > 0) {
      this.#owed = true;
      return 'deferred';
    }
    const id = ++this.#seq;
    this.#current = id;
    this.loading = true;
    try {
      this.#options.onLoadStart?.();
    } catch (e) {
      // A throwing hook must not strand `loading`/`current`: nothing was dispatched, so stand down. It
      // was the current load, so it also ends any hold (else a held key could never be retried).
      if (this.#current === id) this.#current = null;
      this.#currentDone(id);
      this.#idle();
      throw e;
    }
    let value: T;
    try {
      value = await this.#options.fetch();
    } catch (e) {
      if (id !== this.#seq) return 'superseded';
      this.#current = null;
      try {
        this.#options.onLoadError?.(e);
      } finally {
        this.#currentDone(id); // a failed reconcile releases too: holding on would lock the key for good
        this.#idle();
      }
      return 'failed';
    }
    if (id !== this.#seq) return 'superseded';
    this.#current = null;
    try {
      this.#options.commit(value);
      this.hasLoaded = true;
    } finally {
      this.#currentDone(id); // after the commit: a retry now carries the fresh version
      this.#idle();
    }
    return 'committed';
  }

  /**
   * An optimistic (or plain) write. Resolves when the write has settled — the reconcile load it starts
   * is NOT awaited (slim-review on fca#150: `apiFetch` has no timeout, so a stalled GET would withhold
   * the outcome and its toast indefinitely); `settled()` waits for that load. Never rejects for a
   * server error. A throwing callback is rethrown after settle. The failure toast shows at settle, as
   * the refresh starts.
   */
  async mutate<R>(spec: MutationSpec<R>): Promise<MutationOutcome> {
    const { key } = spec;
    if (key !== undefined && this.#pendingKeys.has(key)) return 'skipped';
    let outcome: MutationOutcome = 'ok';
    let localError: { error: unknown } | null = null;
    try {
      // Inside the try: a throwing onLoadIdle (via the retire) still reaches settle.
      this.#enter(key);
      let undo: (() => void) | void = undefined;
      try {
        undo = spec.optimistic?.();
      } catch (e) {
        // A half-applied patch: never send the request; reconcile to server truth.
        localError = { error: e };
        this.#reconcileDue = true;
      }
      if (!localError) {
        let result: R | undefined;
        let succeeded = false;
        try {
          result = await spec.call();
          succeeded = true;
        } catch (e) {
          outcome = classifyFailure(e);
          try {
            undo?.();
          } catch (undoError) {
            localError = { error: undoError };
            this.#reconcileDue = true;
          }
          if (reloadsAfter(spec.reload ?? 'on-reject', outcome)) this.#reconcileDue = true;
        }
        if (succeeded) {
          try {
            spec.commit?.(result as R);
          } catch (commitError) {
            // The server has the write: no undo. Reconcile replaces any half-applied state.
            localError = { error: commitError };
            this.#reconcileDue = true;
          }
          this.#retire();
          if (spec.reload === 'always') this.#reconcileDue = true;
        }
      }
    } catch (e) {
      // Only #enter/#retire can land here (every callback above is individually guarded).
      localError ??= { error: e };
      this.#reconcileDue = true;
    } finally {
      // A rejected/conflicted key stays pending until the reconcile load that follows has finished: until
      // then the undo has put back the OLD row (and its stale version), so a retry would just 409 again.
      // The write itself still resolves now (slim-review delta on fca#150).
      // Hold only when THIS write asked for the reconcile (its own policy, or its own throwing undo) —
      // not because another concurrent write did (council r6): a `reload: 'never'` rejection has no
      // refresh of its own to wait for.
      const ownReconcile = reloadsAfter(spec.reload ?? 'on-reject', outcome) || localError !== null;
      const hold = key !== undefined && ownReconcile && (outcome === 'conflict' || outcome === 'rejected');
      if (hold) this.#heldKeys.add(key);
      this.#settle(hold ? undefined : key);
    }
    const message = outcome === 'ok' ? undefined : spec.messages?.[outcome as 'conflict' | 'rejected' | 'failed'];
    if (message) showToast({ message, kind: outcome === 'failed' ? 'error' : 'info' });
    if (localError) throw localError.error;
    return outcome;
  }

  /**
   * A write with no optimistic step (a dialog save, a write-then-reload). Same window as mutate():
   * loads defer and in-flight loads retire. Rejects with the call's error after settle, so callers can
   * observe the failure; `commit` is applied inside the window.
   */
  async run<R>(spec: RunSpec<R>): Promise<R> {
    let result: R | undefined;
    let failure: { error: unknown } | null = null;
    let localError: { error: unknown } | null = null;
    try {
      this.#enter(undefined);
    } catch (e) {
      // A throwing onLoadIdle (via the retire): settle, reconcile, rethrow — the call is never sent.
      this.#reconcileDue = true;
      this.#settle(undefined);
      throw e;
    }
    try {
      try {
        result = await spec.call();
      } catch (e) {
        failure = { error: e };
        if (reloadsAfter(spec.reload ?? 'always', classifyFailure(e))) this.#reconcileDue = true;
      }
      if (!failure) {
        try {
          spec.commit?.(result as R);
        } catch (commitError) {
          localError = { error: commitError };
          this.#reconcileDue = true;
        }
        this.#retire();
        if ((spec.reload ?? 'always') === 'always') this.#reconcileDue = true;
      }
    } finally {
      this.#settle(undefined);
    }
    if (localError) throw localError.error;
    if (failure) throw failure.error;
    return result as R;
  }

  // ── internals ──────────────────────────────────────────────────────────

  #enter(key: Key | undefined): void {
    this.#writes++;
    if (key !== undefined) {
      const next = new Set(this.#pendingKeys);
      next.add(key);
      this.#pendingKeys = next;
    }
    this.#retire();
  }

  /** Close the window; when it was the last write, START the owed/reconcile load (dispatched now, not awaited). */
  #settle(key: Key | undefined): void {
    this.#writes--;
    if (key !== undefined) {
      const next = new Set(this.#pendingKeys);
      next.delete(key);
      this.#pendingKeys = next;
    }
    if (this.#writes === 0 && (this.#owed || this.#reconcileDue)) {
      this.#owed = false;
      this.#reconcileDue = false;
      let resolve!: () => void;
      const waiter = (this.#settleWaiter ??= { promise: new Promise<void>((r) => (resolve = r)), resolve: () => resolve() });
      this.#settleLoadId = this.#seq + 1; // the id load() is about to take (no write is pending)
      const settleId = this.#settleLoadId;
      // Only the LATEST settle's chain may finish the shared waiter: an older settle load's handler can
      // run after a newer settle has taken the waiter over (slim-review on fca#155).
      const finish = () => {
        if (this.#settleLoadId === settleId) this.#finishSettle(waiter);
      };
      this.load().then(
        (outcome) => {
          // Superseded: the newer load that replaced it resolves the waiter when it lands.
          if (outcome !== 'superseded') finish();
        },
        (e) => {
          // A throwing load callback during the settle reconcile must not surface as the WRITE's result or
          // error (the write already happened), nor as an unhandled rejection. Reported, not swallowed.
          console.error('Resource: the reconcile load after a write failed', e);
          finish();
        },
      );
    }
  }

  #finishSettle(waiter: { resolve: () => void }): void {
    if (this.#settleWaiter === waiter) this.#settleWaiter = null;
    waiter.resolve();
  }

  /**
   * A load finished as current (committed, failed, or its start hook threw): the held keys may be
   * retried with a fresh version, and — unless it is the settle load, which reports through its own
   * chain — it resolves a waiting `settled()`.
   */
  #currentDone(id: number): void {
    this.#releaseHeld();
    if (this.#settleWaiter && id !== this.#settleLoadId) this.#finishSettle(this.#settleWaiter);
  }

  #releaseHeld(): void {
    if (this.#heldKeys.size === 0) return;
    const next = new Set(this.#pendingKeys);
    for (const k of this.#heldKeys) next.delete(k);
    this.#heldKeys.clear();
    this.#pendingKeys = next;
  }

  /** Bump the load id. Only a load REALLY in flight is owed — no request in flight, no extra request. */
  #retire(): void {
    this.#seq++;
    if (this.#current !== null) {
      this.#current = null;
      this.#owed = true;
      this.#idle();
    }
  }

  #idle(): void {
    this.loading = false;
    this.#options.onLoadIdle?.();
  }
}
