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
//                waits for it.
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
export type ReloadPolicy = 'on-reject' | 'always' | 'never';
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
  /** The settle load started by the last write to settle, while it runs. */
  #drain: Promise<void> | null = null;
  readonly #options: ResourceOptions<T>;

  constructor(options: ResourceOptions<T>) {
    this.#options = options;
  }

  get writesPending(): boolean {
    return this.#writes > 0;
  }

  /**
   * Resolves once the settle load (the owed / reconcile load a write started when it settled) has
   * finished — immediately when none is running. Writes don't wait for it (a stalled GET must not
   * withhold a write's outcome or its toast); callers and tests that need the reconciled view do.
   */
  settled(): Promise<void> {
    return this.#drain ?? Promise.resolve();
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
      // A throwing hook must not strand `loading`/`current`: nothing was dispatched, so stand down.
      if (this.#current === id) this.#current = null;
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
      this.#settle(key);
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
      const drain = this.load().then(
        () => undefined,
        (e) => {
          // A throwing load callback during the settle reconcile must not surface as the WRITE's result or
          // error (the write already happened), nor as an unhandled rejection. Reported, not swallowed.
          console.error('Resource: the reconcile load after a write failed', e);
        },
      );
      this.#drain = drain;
      void drain.then(() => {
        if (this.#drain === drain) this.#drain = null;
      });
    }
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
