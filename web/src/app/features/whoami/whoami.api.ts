import { HttpResourceRef, httpResource } from '@angular/common/http';

/** GET /api/me (Me API): what the ACCESS TOKEN says. Mirrors MeResponse in C#. */
export interface MeResponse {
  authenticated: boolean;
  userId: string | null;
  userName: string | null;
  tenantId: string | null;
  roles: string[];
  service: string;
  serverTimeUtc: string;
}

/** GET /api/identity/me (Identity service): what the PLATFORM decides, from memberships. */
export interface PlatformMeResponse {
  userKey: string;
  userName: string | null;
  email: string | null;
  roles: string[];
  tenantId: string;
  customerId: string | null;
  scopes: string[];
  source: string;
}

/*
 * Signal-first data access: each function returns an httpResource, a signal-based view of one
 * HTTP request (value / isLoading / error / reload). No subscribe, no manual state machine.
 *
 * Call them in an injection context (a component field initializer), so the request lives and
 * dies with that component. httpResource goes through HttpClient, so the OIDC authInterceptor
 * still adds the bearer token.
 *
 * Relative URLs only: the dev server proxies /api to the gateway (proxy.conf.js),
 * and the gateway routes /api/me -> me-api, /api/identity/** -> identity.
 */
export function meResource(): HttpResourceRef<MeResponse | undefined> {
  return httpResource<MeResponse>(() => '/api/me');
}

export function platformMeResource(): HttpResourceRef<PlatformMeResponse | undefined> {
  return httpResource<PlatformMeResponse>(() => '/api/identity/me');
}
