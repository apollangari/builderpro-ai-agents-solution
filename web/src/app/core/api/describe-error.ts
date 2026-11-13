import { HttpErrorResponse } from '@angular/common/http';

/**
 * Turns an API error into a sentence a user (and a learner) can act on.
 * Branches on the stable problem+json "code" the services return first, then on HTTP status.
 * Shared by every page, so the same refusal reads the same everywhere.
 */
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
    case 'tenant_lookup_unavailable':
      return 'The platform could not confirm your tenant right now, so nothing is shown (503). Try again shortly.';
  }
  switch (http?.status) {
    case 401:
      return 'The API rejected your token (401). See LEARN 1b, lab C.';
    case 404:
      return 'Not found (404). Is the service running in the AppHost?';
    case 429:
      return 'Too many requests: the gateway rate limit kicked in (429). Wait a minute.';
    default:
      return 'Could not reach the API. Is the AppHost running?';
  }
}
