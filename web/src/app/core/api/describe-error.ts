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
      return 'You are signed in, but not a member of any tenant yet. Ask your retailer for their sign-up link (403 no_membership).';
    case 'store_not_found':
      return "This sign-up link isn't valid or the store isn't accepting sign-ups. Ask your retailer for a new link.";
    case 'invalid_store':
      return "This sign-up link is broken. Ask your retailer for a new link.";
    case 'membership_inactive':
      return 'Your access to this store was removed. Contact the retailer if you think this is a mistake.';
    case 'multiple_active_memberships':
      return 'You belong to several stores. Choose one with the store picker at the top (409).';
    case 'tenant_not_permitted':
      return "You're not a customer of this store, so nothing is shown. Use the store's sign-up link to join.";
    case 'invalid_zip':
      return "That doesn't look like a ZIP code. Enter 5 digits (or a Canadian postal code like K1A 0B1).";
    case 'zip_not_served':
      return "Your store doesn't serve this ZIP yet.";
    case 'feature_not_included':
      return "This isn't part of your store's package. You can still use the step-by-step project flow.";
    case 'missing_scope':
      return "Your account can't do this here: starting and saving projects is for the store's customers (403 missing_scope).";
    case 'customer_only':
      return 'Only the customer can do this (403 customer_only).';
    case 'agent_cannot_accept':
      return 'Only you can accept a quote; the concierge cannot.';
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
    case 403:
      return `You don't have access to this (403${code ? ' ' + code : ''}).`;
    default:
      if ((http?.status ?? 0) >= 500) return `Something went wrong on our side (${http!.status}). Try again in a moment.`;
      if ((http?.status ?? 0) >= 400) return `That didn't work (${http!.status}${code ? ' ' + code : ''}).`;
      return 'Could not reach the API. Is the AppHost running?';
  }
}
