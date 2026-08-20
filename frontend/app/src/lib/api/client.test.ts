// Boundary tests for the SPA's one HTTP client (quest 79aa83e7). Pure fetch-mock
// tests — no DOM: the redirect sink is observed through setNavigateForTesting.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError,
  apiFetch,
  apiGet,
  apiSend,
  apiUpload,
  LOGIN_URL,
  messageFrom,
  setNavigateForTesting,
} from './client';

type FetchArgs = { url: string; init: RequestInit | undefined };

let fetchArgs: FetchArgs[];
let navigations: string[];

function mockFetch(res: Response | (() => Promise<Response>)): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init?: RequestInit) => {
      fetchArgs.push({ url, init });
      return typeof res === 'function' ? res() : res;
    }),
  );
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

let restoreNavigate: () => void;

beforeEach(() => {
  fetchArgs = [];
  navigations = [];
  restoreNavigate = setNavigateForTesting((url) => navigations.push(url));
});

afterEach(() => {
  restoreNavigate();
  vi.unstubAllGlobals();
});

function headersOf(index = 0): Headers {
  return new Headers(fetchArgs[index].init?.headers);
}

describe('parsing', () => {
  it('returns parsed JSON on 200', async () => {
    mockFetch(json(200, { ok: true }));
    await expect(apiGet<{ ok: boolean }>('/api/x')).resolves.toEqual({ ok: true });
  });

  it('returns undefined on 204', async () => {
    mockFetch(new Response(null, { status: 204 }));
    await expect(apiSend<void>('/api/x', 'POST')).resolves.toBeUndefined();
  });
});

describe('error contract', () => {
  it('extracts { message } from a 4xx body', async () => {
    mockFetch(json(400, { message: 'name is required' }));
    await expect(apiGet('/api/x')).rejects.toMatchObject({
      name: 'ApiError',
      status: 400,
      message: 'name is required',
    });
  });

  it('falls back to statusText on an empty body', async () => {
    mockFetch(new Response('', { status: 404, statusText: 'Not Found' }));
    await expect(apiGet('/api/x')).rejects.toMatchObject({ status: 404, message: 'Not Found' });
  });

  it('surfaces a non-JSON body as-is', async () => {
    mockFetch(new Response('plain text failure', { status: 500 }));
    await expect(apiGet('/api/x')).rejects.toMatchObject({ message: 'plain text failure' });
  });

  it('messageFrom: JSON without a usable message yields the raw text', () => {
    expect(messageFrom('{"error":"nope"}')).toBe('{"error":"nope"}');
    expect(messageFrom('')).toBeNull();
  });

  it('classifies 409 as conflict, other 4xx as client rejection', async () => {
    mockFetch(json(409, { message: 'stale' }));
    const conflict = (await apiGet('/api/x').catch((e: unknown) => e)) as ApiError;
    expect(conflict.isConflict).toBe(true);
    expect(conflict.isClientRejection).toBe(false);

    mockFetch(json(400, { message: 'bad' }));
    const rejection = (await apiGet('/api/x').catch((e: unknown) => e)) as ApiError;
    expect(rejection.isConflict).toBe(false);
    expect(rejection.isClientRejection).toBe(true);
  });

  it('propagates a network failure as-is, never as ApiError', async () => {
    mockFetch(() => Promise.reject(new TypeError('Failed to fetch')));
    const e = await apiGet('/api/x').catch((err: unknown) => err);
    expect(e).toBeInstanceOf(TypeError);
    expect(e).not.toBeInstanceOf(ApiError);
  });
});

describe('401 policy', () => {
  it('default: redirects to the login page and never settles', async () => {
    mockFetch(json(401, { message: 'unauthorized' }));
    const outcome = await Promise.race([
      apiGet('/api/x').then(
        () => 'settled',
        () => 'rejected',
      ),
      new Promise((resolve) => setTimeout(() => resolve('pending'), 25)),
    ]);
    expect(outcome).toBe('pending');
    expect(navigations).toEqual([LOGIN_URL]);
  });

  it('carries the current location as ReturnUrl when a window exists', async () => {
    vi.stubGlobal('window', {
      location: { pathname: '/meal-plan', search: '?week=2026-08-17' },
    });
    mockFetch(json(401, { message: 'unauthorized' }));
    void apiGet('/api/x').catch(() => {});
    await new Promise((resolve) => setTimeout(resolve, 5));
    expect(navigations).toEqual([
      `${LOGIN_URL}?ReturnUrl=${encodeURIComponent('/meal-plan?week=2026-08-17')}`,
    ]);
  });

  it("on401: 'throw' throws ApiError 401 and does not redirect", async () => {
    mockFetch(json(401, { message: 'unauthorized' }));
    await expect(apiFetch('/api/me', undefined, { on401: 'throw' })).rejects.toMatchObject({
      name: 'ApiError',
      status: 401,
    });
    expect(navigations).toEqual([]);
  });

  it('403 is NOT redirected — it throws like any rejection (domain signal)', async () => {
    mockFetch(json(403, { message: 'not connected' }));
    await expect(apiGet('/api/x')).rejects.toMatchObject({ status: 403 });
    expect(navigations).toEqual([]);
  });
});

describe('request shaping', () => {
  it('always sends cookies and Accept — including on mutations with a body', async () => {
    mockFetch(json(200, {}));
    await apiSend('/api/x', 'PUT', { name: 'x' });
    const { init } = fetchArgs[0];
    expect(init?.credentials).toBe('include');
    expect(init?.method).toBe('PUT');
    expect(headersOf().get('Accept')).toBe('application/json');
    expect(headersOf().get('Content-Type')).toBe('application/json');
    expect(init?.body).toBe(JSON.stringify({ name: 'x' }));
  });

  it('omits the body entirely when apiSend has none', async () => {
    mockFetch(new Response(null, { status: 204 }));
    await apiSend('/api/x', 'POST');
    expect(fetchArgs[0].init?.body).toBeUndefined();
    expect(headersOf().get('Content-Type')).toBeNull();
  });

  it('preserves caller headers passed as a Headers instance', async () => {
    mockFetch(json(200, {}));
    await apiFetch('/api/x', { headers: new Headers({ 'X-Custom': '1' }) });
    expect(headersOf().get('X-Custom')).toBe('1');
    expect(headersOf().get('Accept')).toBe('application/json');
  });

  it('preserves caller headers passed as a tuple array', async () => {
    mockFetch(json(200, {}));
    await apiFetch('/api/x', { headers: [['X-Custom', '1']] });
    expect(headersOf().get('X-Custom')).toBe('1');
    expect(headersOf().get('Accept')).toBe('application/json');
  });

  it('serializes a body on DELETE (the version-in-body house pattern)', async () => {
    mockFetch(new Response(null, { status: 204 }));
    await apiSend('/api/x', 'DELETE', { version: 7 });
    expect(fetchArgs[0].init?.body).toBe(JSON.stringify({ version: 7 }));
  });

  it('apiUpload never sets Content-Type — the browser owns the boundary', async () => {
    mockFetch(json(200, { photoPath: '/uploads/1/x.jpg' }));
    const form = new FormData();
    form.append('file', new Blob(['x']), 'x.jpg');
    await apiUpload('/api/x/photo', form);
    const { init } = fetchArgs[0];
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe(form);
    expect(headersOf().get('Content-Type')).toBeNull();
  });
});
