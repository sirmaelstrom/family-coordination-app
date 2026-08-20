import type { DashboardDto } from './types';
import { ApiError, apiGet } from '$lib/api/client';

const BASE = '/api/dashboard';

// Transport + error contract live in $lib/api/client (the one HTTP boundary,
// quest 79aa83e7). The dashboard is READ-ONLY (one GET): any 4xx is a
// non-retryable rejection — the store keeps its last good data + calm toast.
export { ApiError };

/** The whole dashboard aggregate (greeting + household + chores + shopping + today's meals). */
export async function getDashboard(): Promise<DashboardDto> {
  return apiGet<DashboardDto>(BASE);
}
