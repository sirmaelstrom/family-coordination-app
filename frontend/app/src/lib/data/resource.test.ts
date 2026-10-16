import { beforeEach, describe, expect, it, vi } from 'vitest';

// Quest 41337b6f — the Resource ordering contract (spec §4.2, S1). Each test names the mechanism it
// guards; the PR's negative controls remove that mechanism and quote the red.

vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { Resource } from './resource.svelte';
import { ApiError } from '$lib/api/client';
import { showToast } from '$lib/shared/toast-store.svelte';

interface Deferred<V> {
  promise: Promise<V>;
  resolve: (v: V) => void;
  reject: (e: unknown) => void;
}
function deferred<V>(): Deferred<V> {
  let resolve!: (v: V) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<V>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}
/** Let pending microtasks run (awaits inside the Resource). */
const tick = () => new Promise<void>((r) => setTimeout(r, 0));

/** A Resource whose fetch hands out one controllable deferred per call. */
function harness() {
  const fetches: Deferred<string>[] = [];
  const committed: string[] = [];
  const errors: unknown[] = [];
  let idles = 0;
  const fetch = vi.fn(() => {
    const d = deferred<string>();
    fetches.push(d);
    return d.promise;
  });
  const res = new Resource<string>({
    fetch,
    commit: (v) => committed.push(v),
    onLoadError: (e) => errors.push(e),
    onLoadIdle: () => idles++,
  });
  return { res, fetch, fetches, committed, errors, idles: () => idles };
}

beforeEach(() => vi.clearAllMocks());

describe('Resource.load — superseded responses', () => {
  it('drops a superseded load result and its error', async () => {
    const h = harness();
    const a = h.res.load();
    const b = h.res.load();
    h.fetches[1].resolve('new');
    expect(await b).toBe('committed');
    h.fetches[0].reject(new Error('old failure'));
    expect(await a).toBe('superseded');
    expect(h.committed).toEqual(['new']);
    expect(h.errors).toEqual([]);
    expect(h.res.loading).toBe(false);
    expect(h.res.hasLoaded).toBe(true);
  });
});

describe('Resource — deferral: no load is dispatched while a write is pending', () => {
  it('load() during a write dispatches nothing, then exactly one fetch runs at settle', async () => {
    const h = harness();
    const call = deferred<void>();
    const writing = h.res.mutate({ call: () => call.promise });

    expect(await h.res.load()).toBe('deferred');
    expect(await h.res.load()).toBe('deferred'); // ticks coalesce
    expect(h.fetch).toHaveBeenCalledTimes(0);

    call.resolve();
    await tick();
    expect(h.fetch).toHaveBeenCalledTimes(1);
    h.fetches[0].resolve('after');
    expect(await writing).toBe('ok');
    expect(h.committed).toEqual(['after']);
  });

  it('two overlapping writes settle into one owed fetch, after the last', async () => {
    const h = harness();
    const c1 = deferred<void>();
    const c2 = deferred<void>();
    const w1 = h.res.mutate({ key: 'a', call: () => c1.promise });
    const w2 = h.res.mutate({ key: 'b', call: () => c2.promise });
    await h.res.load();
    c1.resolve();
    await w1;
    expect(h.fetch).toHaveBeenCalledTimes(0); // b still pending
    c2.resolve();
    await tick();
    expect(h.fetch).toHaveBeenCalledTimes(1);
    h.fetches[0].resolve('x');
    await w2;
  });

  it('zero extra requests: a write with no load in flight and nothing owed triggers no fetch', async () => {
    const h = harness();
    expect(await h.res.mutate({ call: async () => 1, commit: () => {} })).toBe('ok');
    expect(h.fetch).toHaveBeenCalledTimes(0);
  });
});

describe('Resource — retirement of a load already in flight (the fca#103 residual)', () => {
  for (const order of ['GET answers before the write', 'GET answers after the write'] as const) {
    for (const path of ['success', 'failure'] as const) {
      it(`${order}, write ${path}: the stale payload never commits; one owed load runs at settle`, async () => {
        const h = harness();
        const loading = h.res.load(); // a liveness GET, dispatched before the write
        expect(h.res.loading).toBe(true);
        const call = deferred<void>();
        const writing = h.res.mutate({ call: () => call.promise, reload: 'never' });
        expect(h.res.loading).toBe(false); // retired: no current load remains

        const settleWrite = () =>
          path === 'success' ? call.resolve() : call.reject(new ApiError(500, 'boom'));
        if (order === 'GET answers before the write') {
          h.fetches[0].resolve('STALE');
          expect(await loading).toBe('superseded');
          settleWrite();
        } else {
          settleWrite();
          await tick();
          h.fetches[0].resolve('STALE');
          expect(await loading).toBe('superseded');
        }
        await tick();
        expect(h.fetch).toHaveBeenCalledTimes(2); // the owed load
        h.fetches[1].resolve('fresh');
        await writing;
        expect(h.committed).toEqual(['fresh']);
      });
    }
  }
});

describe('Resource.mutate — optimistic, commit, the three-way branch', () => {
  it('applies the optimistic patch synchronously, then commits the response', async () => {
    const h = harness();
    const log: string[] = [];
    const call = deferred<string>();
    const writing = h.res.mutate({
      optimistic: () => {
        log.push('patch');
        return () => log.push('undo');
      },
      call: () => call.promise,
      commit: (r) => log.push(`commit:${r}`),
    });
    expect(log).toEqual(['patch']); // before any await
    call.resolve('dto');
    expect(await writing).toBe('ok');
    expect(log).toEqual(['patch', 'commit:dto']);
  });

  const cases = [
    { name: '409', error: new ApiError(409, 'conflict'), outcome: 'conflict', reconcile: true, kind: 'info' },
    { name: 'other 4xx', error: new ApiError(404, 'gone'), outcome: 'rejected', reconcile: true, kind: 'info' },
    { name: '5xx', error: new ApiError(503, 'down'), outcome: 'failed', reconcile: false, kind: 'error' },
    { name: 'network', error: new TypeError('Failed to fetch'), outcome: 'failed', reconcile: false, kind: 'error' },
  ] as const;
  for (const c of cases) {
    it(`${c.name}: undoes, returns '${c.outcome}', reconciles=${c.reconcile} under 'on-reject', toasts ${c.kind}`, async () => {
      const h = harness();
      const undo = vi.fn();
      const writing = h.res.mutate({
        optimistic: () => undo,
        call: () => Promise.reject(c.error),
        messages: { conflict: 'C', rejected: 'R', failed: 'F' },
      });
      await tick();
      if (c.reconcile) h.fetches[0]?.resolve('truth');
      expect(await writing).toBe(c.outcome);
      expect(undo).toHaveBeenCalledTimes(1);
      expect(h.fetch).toHaveBeenCalledTimes(c.reconcile ? 1 : 0);
      expect(showToast).toHaveBeenCalledWith(expect.objectContaining({ kind: c.kind }));
    });
  }

  it("reload policy: 'always' reconciles a 5xx and a success; 'never' skips a 409", async () => {
    const h = harness();
    const w1 = h.res.mutate({ call: () => Promise.reject(new ApiError(500, 'x')), reload: 'always' });
    await tick();
    h.fetches[0].resolve('a');
    await w1;
    const w2 = h.res.mutate({ call: async () => 1, reload: 'always' });
    await tick();
    h.fetches[1].resolve('b');
    await w2;
    await h.res.mutate({ call: () => Promise.reject(new ApiError(409, 'x')), reload: 'never' });
    expect(h.fetch).toHaveBeenCalledTimes(2);
  });

  it("reload policy 'on-error': every failure reconciles (409, 4xx, 5xx, network), a success does not", async () => {
    const failures = [new ApiError(409, 'c'), new ApiError(404, 'r'), new ApiError(503, 'f'), new TypeError('net')];
    for (const [i, e] of failures.entries()) {
      const h = harness();
      await h.res.mutate({ call: () => Promise.reject(e), reload: 'on-error' });
      expect(h.fetch, `failure #${i}`).toHaveBeenCalledTimes(1);
      h.fetches[0].resolve('truth');
      await h.res.settled();
    }
    const ok = harness();
    expect(await ok.res.mutate({ call: async () => 1, reload: 'on-error' })).toBe('ok');
    expect(ok.fetch).not.toHaveBeenCalled();
    // run() honours it too: a failure reconciles, a success doesn't.
    const r = harness();
    await expect(r.res.run({ call: () => Promise.reject(new TypeError('net')), reload: 'on-error' })).rejects.toThrow('net');
    expect(r.fetch).toHaveBeenCalledTimes(1);
    r.fetches[0].resolve('truth');
    await r.res.settled();
    await r.res.run({ call: async () => 1, reload: 'on-error' });
    expect(r.fetch).toHaveBeenCalledTimes(1);
  });

  it("a rejected 'never' write is not held just because another write asked for a reconcile (council r6)", async () => {
    const h = harness();
    const other = deferred<void>();
    const open = h.res.mutate({ key: 'a', call: () => other.promise }); // keeps the window open
    // Another write's 409 has asked for a reconcile (it is owed at the last settle)…
    expect(await h.res.mutate({ key: 'c', call: () => Promise.reject(new ApiError(409, 'c')) })).toBe('conflict');
    // …so a 'never' rejection settling now must not be held on THAT write's reconcile.
    expect(await h.res.mutate({ key: 'b', call: () => Promise.reject(new ApiError(400, 'r')), reload: 'never' })).toBe(
      'rejected',
    );
    expect(h.res.isPending('c')).toBe(true); // its own reconcile is still to come
    expect(h.res.isPending('b')).toBe(false); // no refresh of its own to wait for
    other.resolve();
    await open;
    h.fetches[0]?.resolve('truth');
    await h.res.settled();
  });

  it('a duplicate key is skipped: no optimistic step, no call, no toast', async () => {
    const h = harness();
    const call = deferred<void>();
    const first = h.res.mutate({ key: 7, call: () => call.promise });
    const optimistic = vi.fn();
    const second = vi.fn(async () => {});
    expect(await h.res.mutate({ key: 7, optimistic, call: second, messages: { failed: 'F' } })).toBe('skipped');
    expect(optimistic).not.toHaveBeenCalled();
    expect(second).not.toHaveBeenCalled();
    call.resolve();
    await first;
    expect(showToast).not.toHaveBeenCalled();
  });

  it('isPending flips true then false on every exit path, replacing the set each time', async () => {
    const h = harness();
    for (const settle of [
      () => Promise.resolve(),
      () => Promise.reject(new ApiError(409, 'c')),
      () => Promise.reject(new TypeError('net')),
    ]) {
      const call = deferred<void>();
      const before = h.res.pendingKeys;
      const writing = h.res.mutate({ key: 'k', call: () => call.promise, reload: 'never' });
      expect(h.res.isPending('k')).toBe(true);
      const during = h.res.pendingKeys;
      expect(during).not.toBe(before); // copy-on-write: an in-place add would not notify readers
      settle().then(call.resolve, call.reject);
      await writing;
      expect(h.res.isPending('k')).toBe(false);
      expect(h.res.pendingKeys).not.toBe(during);
      expect(h.res.writesPending).toBe(false);
    }
  });
});

describe('Resource — local callback failures are not server failures', () => {
  it('a commit that throws after a successful call: no undo, a reconcile, settled, rethrown', async () => {
    const h = harness();
    const undo = vi.fn();
    const boom = new Error('commit bug');
    const writing = h.res.mutate({
      key: 'k',
      optimistic: () => undo,
      call: async () => 'dto',
      commit: () => {
        throw boom;
      },
    });
    await expect(writing).rejects.toBe(boom);
    expect(h.fetch).toHaveBeenCalledTimes(1); // the reconcile
    h.fetches[0].resolve('truth');
    await h.res.settled();
    expect(h.committed).toEqual(['truth']);
    expect(undo).not.toHaveBeenCalled();
    expect(h.res.isPending('k')).toBe(false);
    expect(h.res.writesPending).toBe(false);
  });

  it('a throwing optimistic never sends the request; settles, reconciles and rethrows', async () => {
    const h = harness();
    const call = vi.fn(async () => {});
    const boom = new Error('patch bug');
    const writing = h.res.mutate({
      optimistic: () => {
        throw boom;
      },
      call,
    });
    await expect(writing).rejects.toBe(boom);
    expect(h.fetch).toHaveBeenCalledTimes(1); // the reconcile
    h.fetches[0].resolve('truth');
    await h.res.settled();
    expect(call).not.toHaveBeenCalled();
    expect(h.res.writesPending).toBe(false);
  });

  it('a throwing undo still settles and rethrows', async () => {
    const h = harness();
    const boom = new Error('undo bug');
    const writing = h.res.mutate({
      optimistic: () => () => {
        throw boom;
      },
      call: () => Promise.reject(new TypeError('net')),
    });
    await expect(writing).rejects.toBe(boom);
    h.fetches[0].resolve('truth');
    await h.res.settled();
    expect(h.res.writesPending).toBe(false);
  });
});

describe('Resource — throwing hooks never strand the window (code council r2)', () => {
  it('a throwing onLoadIdle during a write-start retire still settles mutate and run', async () => {
    let throwIdle = false;
    const res = new Resource<string>({
      // Resolves on a later microtask: still in flight when the write starts synchronously below.
      fetch: async () => 'x',
      commit: () => {},
      onLoadIdle: () => {
        if (throwIdle) throw new Error('idle hook bug');
      },
    });
    for (const write of [
      () => res.mutate({ key: 'k', call: async () => 1 }),
      () => res.run({ call: async () => 1 }),
    ]) {
      void res.load(); // a load in flight, so the write's retire calls onLoadIdle
      throwIdle = true;
      await expect(write()).rejects.toThrow('idle hook bug');
      throwIdle = false;
      expect(res.writesPending).toBe(false);
      expect(res.isPending('k')).toBe(false);
      await tick();
      expect(await res.load()).not.toBe('deferred'); // later loads are not stuck behind a phantom write
    }
  });

  it('a throwing onLoadStart leaves nothing stranded', async () => {
    let throwStart = true;
    const fetch = vi.fn(async () => 'v');
    const res = new Resource<string>({
      fetch,
      commit: () => {},
      onLoadStart: () => {
        if (throwStart) throw new Error('start hook bug');
      },
    });
    await expect(res.load()).rejects.toThrow('start hook bug');
    expect(res.loading).toBe(false);
    expect(fetch).not.toHaveBeenCalled();
    throwStart = false;
    expect(await res.mutate({ call: async () => 1 })).toBe('ok'); // nothing owed: no phantom load
    expect(fetch).not.toHaveBeenCalled();
    expect(await res.load()).toBe('committed');
  });

  it("a throwing commit in the settle reconcile doesn't replace the write's own outcome", async () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const res = new Resource<string>({
      fetch: async () => 'v',
      commit: () => {
        throw new Error('load commit bug');
      },
    });
    expect(await res.mutate({ call: () => Promise.reject(new ApiError(409, 'c')) })).toBe('conflict');
    await res.settled();
    expect(spy).toHaveBeenCalledWith('Resource: the reconcile load after a write failed', expect.any(Error));
    spy.mockRestore();
  });

  it('a conflicted key stays pending until its reconcile has landed (no stale-version retry)', async () => {
    const h = harness();
    expect(await h.res.mutate({ key: 'e1', call: () => Promise.reject(new ApiError(409, 'c')) })).toBe('conflict');
    expect(h.res.isPending('e1')).toBe(true); // the old row (and version) is back until the GET lands
    const retry = vi.fn(async () => {});
    expect(await h.res.mutate({ key: 'e1', call: retry })).toBe('skipped');
    expect(retry).not.toHaveBeenCalled();
    h.fetches[0].resolve('fresh');
    await h.res.settled();
    expect(h.res.isPending('e1')).toBe(false);
    expect(await h.res.mutate({ key: 'e1', call: retry })).toBe('ok');
    // A 5xx/network failure has no reconcile to wait for: its key is released at once.
    expect(await h.res.mutate({ key: 'e2', call: () => Promise.reject(new TypeError('net')) })).toBe('failed');
    expect(h.res.isPending('e2')).toBe(false);
  });

  // Council r3 (fca#151) and r4 (fca#153): the hold follows the CURRENT load, not the reconcile promise.
  it('a superseded reconcile does not release a held key; the load that supersedes it does', async () => {
    const h = harness();
    expect(await h.res.mutate({ key: 'e1', call: () => Promise.reject(new ApiError(409, 'c')) })).toBe('conflict');
    const b = h.res.load(); // a liveness tick supersedes the reconcile (fetches[0])
    h.fetches[0].resolve('reconcile, superseded');
    await tick();
    expect(h.res.isPending('e1')).toBe(true); // nothing fresh has landed yet
    h.fetches[1].resolve('fresh');
    expect(await b).toBe('committed');
    expect(h.res.isPending('e1')).toBe(false);
    expect(h.committed).toEqual(['fresh']);
  });

  it('a stalled superseded reconcile does not keep the key locked once a newer load has landed', async () => {
    const h = harness();
    expect(await h.res.mutate({ key: 'e1', call: () => Promise.reject(new ApiError(409, 'c')) })).toBe('conflict');
    const b = h.res.load(); // supersedes the reconcile, which never answers
    h.fetches[1].resolve('fresh');
    expect(await b).toBe('committed');
    expect(h.res.isPending('e1')).toBe(false);
  });

  it("a reconcile retired by another write holds the key until that write's owed load lands", async () => {
    const h = harness();
    expect(await h.res.mutate({ key: 'e1', call: () => Promise.reject(new ApiError(409, 'c')) })).toBe('conflict');
    const w = deferred<void>();
    const writing = h.res.mutate({ key: 'e2', call: () => w.promise }); // retires the reconcile (fetches[0])
    h.fetches[0].resolve('stale reconcile');
    await tick();
    expect(h.res.isPending('e1')).toBe(true);
    w.resolve();
    expect(await writing).toBe('ok');
    expect(h.fetch).toHaveBeenCalledTimes(2); // the owed load at e2's settle
    expect(h.res.isPending('e1')).toBe(true);
    h.fetches[1].resolve('fresh');
    await h.res.settled();
    expect(h.res.isPending('e1')).toBe(false);
    expect(h.committed).toEqual(['fresh']);
  });

  it('settled() follows the load that superseded the settle load, in both response orders (council r5)', async () => {
    const pending = (p: Promise<void>) => Promise.race([p.then(() => 'settled'), tick().then(() => 'pending')]);
    // (a) the superseded settle load answers first: settled() still waits for the successor.
    // The promise taken BEFORE the successor lands is the one asserted (slim-review on fca#155): a fresh
    // settled() call afterwards would pass even if the original promise were orphaned.
    const h = harness();
    await h.res.mutate({ key: 'e1', call: () => Promise.reject(new ApiError(409, 'c')) });
    const s1 = h.res.settled();
    const b = h.res.load();
    h.fetches[0].resolve('superseded');
    expect(await pending(s1)).toBe('pending');
    h.fetches[1].resolve('fresh');
    await b;
    expect(await pending(s1)).toBe('settled');
    // (b) the superseded settle load stalls: the successor landing resolves settled().
    const g = harness();
    await g.res.mutate({ key: 'e1', call: () => Promise.reject(new ApiError(409, 'c')) });
    const s2 = g.res.settled();
    const c = g.res.load();
    g.fetches[1].resolve('fresh');
    await c;
    expect(await pending(s2)).toBe('settled');
  });

  it("an older settle load's handler never finishes the waiter a newer settle owns (slim-review on fca#155)", async () => {
    const pending = (p: Promise<void>) => Promise.race([p.then(() => 'settled'), tick().then(() => 'pending')]);
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    let throwStart = true;
    const fetches: Deferred<string>[] = [];
    const res = new Resource<string>({
      fetch: () => {
        const d = deferred<string>();
        fetches.push(d);
        return d.promise;
      },
      commit: () => {},
      onLoadStart: () => {
        if (throwStart) {
          throwStart = false;
          throw new Error('start boom');
        }
      },
    });
    // Two writes whose calls THROW synchronously with a 409, back to back (astra's repro): each mutate
    // settles before returning, so they are two separate settles. The first settle's load throws at its
    // start hook; the second settle's load is in flight when the first's rejection handler runs.
    const conflict = (): Promise<void> => {
      throw new ApiError(409, 'c');
    };
    const first = res.mutate({ key: 'e1', call: conflict });
    const second = res.mutate({ key: 'e2', call: conflict });
    await first;
    await second;
    const s = res.settled();
    await tick();
    expect(fetches).toHaveLength(1); // the newer settle's reconcile is in flight
    expect(res.isPending('e2')).toBe(true);
    expect(await pending(s)).toBe('pending');
    fetches[0].resolve('fresh');
    await s;
    expect(res.isPending('e2')).toBe(false);
    spy.mockRestore();
  });

  it('a held key is released when the reconcile start hook or commit throws', async () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    let throwStart = false;
    let throwCommit = false;
    const fetch = vi.fn(async () => 'v');
    const res = new Resource<string>({
      fetch,
      commit: () => {
        if (throwCommit) throw new Error('commit boom');
      },
      onLoadStart: () => {
        if (throwStart) throw new Error('start boom');
      },
    });
    throwStart = true;
    await res.mutate({ key: 'e1', call: () => Promise.reject(new ApiError(409, 'c')) });
    await res.settled();
    expect(res.isPending('e1')).toBe(false);
    throwStart = false;
    throwCommit = true;
    await res.mutate({ key: 'e2', call: () => Promise.reject(new ApiError(409, 'c')) });
    await res.settled();
    expect(res.isPending('e2')).toBe(false);
    expect(spy).toHaveBeenCalledTimes(2);
    spy.mockRestore();
  });

  it('a failed reconcile releases the key (holding on would lock it for good)', async () => {
    const h = harness();
    expect(await h.res.mutate({ key: 'e1', call: () => Promise.reject(new ApiError(409, 'c')) })).toBe('conflict');
    h.fetches[0].reject(new TypeError('net'));
    await h.res.settled();
    expect(h.res.isPending('e1')).toBe(false);
  });

  it("a stalled reconcile GET withholds neither the write's outcome nor its toast (slim-review fca#150)", async () => {
    const fetch = vi.fn(() => new Promise<string>(() => {})); // apiFetch has no timeout
    const res = new Resource<string>({ fetch, commit: () => {} });
    const outcome = await Promise.race([
      res.mutate({ call: () => Promise.reject(new ApiError(409, 'c')), messages: { conflict: 'C' } }),
      new Promise((r) => setTimeout(() => r('STILL PENDING'), 50)),
    ]);
    expect(outcome).toBe('conflict');
    expect(fetch).toHaveBeenCalledTimes(1); // the reconcile was dispatched…
    expect(showToast).toHaveBeenCalledWith({ message: 'C', kind: 'info' }); // …and the toast didn't wait for it
    const settledYet = await Promise.race([res.settled().then(() => 'settled'), tick().then(() => 'pending')]);
    expect(settledYet).toBe('pending');
  });
});

describe('Resource.run — the write window without an optimistic step', () => {
  it("reload 'never': a failure rejects without a reconcile", async () => {
    const h = harness();
    await expect(h.res.run({ call: () => Promise.reject(new ApiError(500, 'x')), reload: 'never' })).rejects.toThrow('x');
    expect(h.fetch).not.toHaveBeenCalled();
    expect(h.res.writesPending).toBe(false);
  });

  it('defers and retires like mutate, commits inside the window, reloads by default', async () => {
    const h = harness();
    const loading = h.res.load();
    const call = deferred<string>();
    const order: string[] = [];
    const running = h.res.run({ call: () => call.promise, commit: (r) => order.push(`commit:${r}`) });
    expect(await h.res.load()).toBe('deferred');
    h.fetches[0].resolve('STALE');
    expect(await loading).toBe('superseded');
    call.resolve('dto');
    await tick();
    expect(h.fetch).toHaveBeenCalledTimes(2);
    h.fetches[1].resolve('fresh');
    expect(await running).toBe('dto');
    await h.res.settled();
    expect(order).toEqual(['commit:dto']);
    expect(h.committed).toEqual(['fresh']);
  });

  it('rejects with the call error after settle, having started the reconcile', async () => {
    const h = harness();
    const err = new ApiError(400, 'bad');
    await expect(h.res.run({ call: () => Promise.reject(err) })).rejects.toBe(err);
    expect(h.res.writesPending).toBe(false);
    expect(h.fetch).toHaveBeenCalledTimes(1);
    h.fetches[0].resolve('truth');
    await h.res.settled();
    expect(h.committed).toEqual(['truth']);
  });
});
