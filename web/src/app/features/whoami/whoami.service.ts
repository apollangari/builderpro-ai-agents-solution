import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

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

/**
 * Relative URLs only: the dev server proxies /api to the gateway (proxy.conf.js),
 * and the gateway routes /api/me -> me-api, /api/identity/** -> identity.
 */
@Injectable({ providedIn: 'root' })
export class WhoAmIService {
  private readonly http = inject(HttpClient);

  getMe(): Observable<MeResponse> {
    return this.http.get<MeResponse>('/api/me');
  }

  getPlatformMe(): Observable<PlatformMeResponse> {
    return this.http.get<PlatformMeResponse>('/api/identity/me');
  }
}

/** Turns an API error into a sentence a user (and a learner) can act on. */
export function describeError(err: HttpErrorResponse | { status?: number; error?: unknown }): string {
  const code = (err.error as { code?: string } | null)?.code;
  switch (code) {
    case 'no_membership':
      return 'You are signed in, but not a member of any tenant yet (403 no_membership).';
    case 'multiple_active_memberships':
      return 'You belong to several tenants and the token does not say which one (409).';
    case 'tenant_not_permitted':
      return 'Your token names a tenant you are not a member of (403 tenant_not_permitted).';
  }
  switch (err.status) {
    case 401:
      return 'The API rejected your token (401). See LEARN 1b, lab C.';
    case 429:
      return 'Too many requests: the gateway rate limit kicked in (429). Wait a minute.';
    default:
      return 'Could not reach the API. Is the AppHost running?';
  }
}
