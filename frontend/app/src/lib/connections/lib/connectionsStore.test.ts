import { beforeEach, describe, expect, it, vi } from 'vitest';

// Quest 41337b6f, slice 2 (spec §5 row 5): the connections store on the Resource. generate / cancel
// used to write local state without retiring a load in flight, so that load could land afterwards and
// revert the write; accept / disconnect wrote, then reloaded, with the same overlap.

// The REAL ApiError: the Resource classifies failures through its getters.
vi.mock('./api', async () => {
  const { ApiError } = await vi.importActual<typeof import('$lib/api/client')>('$lib/api/client');
  return {
    ApiError,
    getConnections: vi.fn(),
    generateInvite: vi.fn(),
    cancelInvite: vi.fn(),
    validateCode: vi.fn(),
    acceptCode: vi.fn(),
    disconnect: vi.fn(),
  };
});
vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { connectionsStore } from './connectionsStore.svelte';
import { ApiError, acceptCode, cancelInvite, disconnect, generateInvite, getConnections } from './api';
import { showToast } from '$lib/shared/toast-store.svelte';
import type { ConnectedDto, ConnectionsDto, InviteDto } from './types';

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (err: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}
/** Let the settle load (dispatched, not awaited, by the write) run to completion. */
const flush = () => new Promise<void>((r) => setTimeout(r, 0));

const INVITE: InviteDto = { code: 'ABC123', expiresAt: '2026-10-20T00:00:00Z' } as InviteDto;
const OTHER: ConnectedDto = { householdId: 7, householdName: 'Others', connectedAt: '2026-10-01T00:00:00Z' } as ConnectedDto;
const dto = (activeInvite: InviteDto | null, connected: ConnectedDto[] = []): ConnectionsDto => ({ activeInvite, connected });

describe('connectionsStore on the Resource (41337b6f slice 2)', () => {
  beforeEach(async () => {
    vi.resetAllMocks(); // also drops unconsumed *Once answers, so none leaks into the next step
    vi.mocked(getConnections).mockResolvedValueOnce(dto(null));
    await connectionsStore.load();
    await flush();
    vi.resetAllMocks(); // also drops unconsumed *Once answers, so none leaks into the next step
  });

  it('a load in flight when generate starts never lands over the new invite; it is owed once', async () => {
    const stale = deferred<ConnectionsDto>();
    vi.mocked(getConnections).mockReturnValueOnce(stale.promise).mockResolvedValueOnce(dto(INVITE));
    vi.mocked(generateInvite).mockResolvedValueOnce(INVITE);

    const loading = connectionsStore.load();
    await connectionsStore.generate();
    expect(connectionsStore.activeInvite).toEqual(INVITE);

    stale.resolve(dto(null)); // pre-generate truth: no invite
    await loading;
    await flush();

    expect(connectionsStore.activeInvite).toEqual(INVITE);
    expect(getConnections).toHaveBeenCalledTimes(2);
  });

  it('generate with no load in flight sends no extra GET (reload never)', async () => {
    vi.mocked(generateInvite).mockResolvedValueOnce(INVITE);
    await connectionsStore.generate();
    await flush();
    expect(getConnections).not.toHaveBeenCalled();
    expect(connectionsStore.activeInvite).toEqual(INVITE);
    expect(connectionsStore.generating).toBe(false);
  });

  it('a load requested while a cancel is pending is deferred, then runs once at settle', async () => {
    connectionsStore.activeInvite = INVITE;
    const del = deferred<void>();
    vi.mocked(cancelInvite).mockReturnValueOnce(del.promise);
    vi.mocked(getConnections).mockResolvedValueOnce(dto(null));

    const cancelling = connectionsStore.cancelActiveInvite();
    await connectionsStore.load();
    expect(getConnections).not.toHaveBeenCalled();

    del.resolve();
    await cancelling;
    await flush();
    expect(getConnections).toHaveBeenCalledTimes(1);
    expect(connectionsStore.activeInvite).toBeNull();
  });

  it('a business "no" from accept shows at once, even while the refresh stalls', async () => {
    connectionsStore.setCode('xyz789');
    connectionsStore.showConfirm = true;
    vi.mocked(acceptCode).mockResolvedValueOnce({ success: false, connectedHouseholdName: null, error: 'expired' });
    const refresh = deferred<ConnectionsDto>();
    vi.mocked(getConnections).mockReturnValueOnce(refresh.promise); // a stalled refresh

    await connectionsStore.accept();

    expect(connectionsStore.codeError).toBe('This code has expired. Ask them to create a new one.');
    expect(connectionsStore.showConfirm).toBe(false);
    expect(connectionsStore.accepting).toBe(false);
    expect(getConnections).toHaveBeenCalledTimes(1); // the refresh WAS dispatched; nothing waited on it
    refresh.resolve(dto(null, []));
    await flush();
  });

  it('accept closes the confirm on success and refreshes the list once after the write settles', async () => {
    connectionsStore.setCode('xyz789');
    connectionsStore.showConfirm = true;
    vi.mocked(acceptCode).mockResolvedValueOnce({ success: true, connectedHouseholdName: 'Others', error: null });
    vi.mocked(getConnections).mockResolvedValueOnce(dto(null, [OTHER]));

    await connectionsStore.accept();
    expect(connectionsStore.showConfirm).toBe(false);
    await flush();

    expect(acceptCode).toHaveBeenCalledWith('XYZ789');
    expect(getConnections).toHaveBeenCalledTimes(1);
    expect(connectionsStore.connected).toEqual([OTHER]);
    expect(connectionsStore.enteredCode).toBe('');
    expect(connectionsStore.accepting).toBe(false);
  });

  it('a confirmed disconnect drops its row at once, before the refresh lands', async () => {
    connectionsStore.connected = [OTHER];
    connectionsStore.askDisconnect(OTHER);
    vi.mocked(disconnect).mockResolvedValueOnce(undefined);
    const refresh = deferred<ConnectionsDto>();
    vi.mocked(getConnections).mockReturnValueOnce(refresh.promise); // a stalled refresh

    await connectionsStore.confirmDisconnect();

    expect(connectionsStore.connected).toEqual([]);
    expect(connectionsStore.disconnectTarget).toBeNull();
    expect(showToast).toHaveBeenCalledWith({ message: 'Stopped sharing with Others', kind: 'success' });
    expect(getConnections).toHaveBeenCalledTimes(1);
    refresh.resolve(dto(null, []));
    await flush();
  });

  it('a failed disconnect toasts and reconciles the list', async () => {
    connectionsStore.connected = [OTHER];
    connectionsStore.askDisconnect(OTHER);
    vi.mocked(disconnect).mockRejectedValueOnce(new ApiError(404, 'Not connected.'));
    vi.mocked(getConnections).mockResolvedValueOnce(dto(null, []));

    await connectionsStore.confirmDisconnect();
    await flush();

    expect(showToast).toHaveBeenCalledWith({ message: 'Not connected.', kind: 'error' });
    expect(getConnections).toHaveBeenCalledTimes(1);
    expect(connectionsStore.connected).toEqual([]);
    expect(connectionsStore.disconnectTarget).toBeNull();
  });
});
