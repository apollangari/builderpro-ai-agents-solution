import { HttpErrorResponse, HttpResourceRef, httpResource } from '@angular/common/http';

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

/** Turns an API error into a sentence a user (and a learner) can act on. */
export function describeError(err: HttpErrorResponse | { status?: number; error?: unknown } | Error | undefined): string {
  const http = err as { status?: number; error?: unknown } | undefined;
  const code = (http?.error as { code?: string } | null | undefined)?.code;
  switch (code) {
    case 'no_membership':
      return 'You are signed in, but not a member of any tenant yet (403 no_membership).';
    case 'multiple_active_memberships':
      return 'You belong to several tenants and the token does not say which one (409).';
    case 'tenant_not_permitted':
      return 'Your token names a tenant you are not a member of (403 tenant_not_permitted).';
  }
  switch (http?.status) {
    case 401:
      return 'The API rejected your token (401). See LEARN 1b, lab C.';
    case 429:
      return 'Too many requests: the gateway rate limit kicked in (429). Wait a minute.';
    default:
      return 'Could not reach the API. Is the AppHost running?';
  }
}
