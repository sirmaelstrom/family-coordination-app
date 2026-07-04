import type { DashboardDto } from './types';

const BASE = '/api/dashboard';

/**
 * Thrown on any non-2xx response. The dashboard is READ-ONLY (one GET), so any
 * 4xx is simply a non-retryable rejection — the store keeps its last good data
 * and surfaces a calm toast. (Carried from the other islands: the app can
 * re-execute empty-body 404s as empty 400s — `isClientRejection` covers any 4xx.)
 */
export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  get isClientRejection(): boolean {
    return this.status >= 400 && this.status < 500;
  }
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    // Same-origin cookie auth — the host page is already authenticated.
    credentials: 'include',
    headers: { Accept: 'application/json', ...(init?.headers ?? {}) },
    ...init,
  });
  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new ApiError(res.status, text || res.statusText);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

/** The whole dashboard aggregate (greeting + household + chores + shopping + today's meals). */
export async function getDashboard(): Promise<DashboardDto> {
  return request<DashboardDto>(BASE);
}
