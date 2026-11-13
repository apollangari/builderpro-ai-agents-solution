import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';

/** The ID token claims this page shows (Keycloak mappers on the builderpro-web client). */
interface ProfileClaims {
  name?: string;
  email?: string;
  phone?: string;
  address_line1?: string;
  address_line2?: string;
  city?: string;
  region?: string;
  postal_code?: string;
  country?: string;
  contact_consent?: string;
  contact_hours?: string;
  /** Set when the user signed in through another provider (e.g. "google"); absent for email + password. */
  identity_provider?: string;
}

const CONSENT: Record<string, string> = {
  call: 'Phone calls',
  text: 'Text messages (SMS)',
  both: 'Calls and texts',
  none: 'Email only, no calls or texts',
};
const HOURS: Record<string, string> = {
  business: 'Business hours (Mon-Fri, 9am-5pm)',
  evenings: 'Weekday evenings (5pm-8pm)',
  weekends: 'Weekends (9am-5pm)',
  anytime: 'Any time during business or evening hours',
};

/**
 * Account (owner request): the customer's profile, with "Edit profile" and "Change password".
 * Both open Keycloak's own pages through application-initiated actions (kc_action), styled with
 * the Navy & Sand login theme, so passwords and profile validation never pass through this app.
 * Change password is offered only to email + password accounts: a Google sign-in has no password here.
 */
@Component({
  selector: 'bp-account-page',
  imports: [RouterLink],
  template: `
    <div class="page narrow">
      <header class="page-head">
        <span class="eyebrow">Account</span>
        <h1>Your profile</h1>
      </header>

      <section class="card" data-testid="profile-card">
        <dl class="facts">
          <dt>Name</dt><dd data-testid="profile-name">{{ p().name || '—' }}</dd>
          <dt>Email</dt><dd>{{ p().email || '—' }}</dd>
          <dt>Mobile phone</dt><dd data-testid="profile-phone">{{ p().phone || '—' }}</dd>
          <dt>Project address</dt>
          <dd data-testid="profile-address">
            @if (address()) { {{ address() }} } @else { — }
          </dd>
          <dt>How we may contact you</dt><dd data-testid="profile-consent">{{ consent() }}</dd>
          <dt>Best time to reach you</dt><dd>{{ hours() }}</dd>
          <dt>Signed in with</dt><dd data-testid="profile-method">{{ method() }}</dd>
        </dl>
        <div class="actions">
          <button type="button" class="btn btn-primary" (click)="editProfile()" data-testid="edit-profile">Edit profile</button>
          @if (hasPassword()) {
            <button type="button" class="btn" (click)="changePassword()" data-testid="change-password">Change password</button>
          }
        </div>
        @if (!hasPassword()) {
          <p class="muted small" data-testid="google-note">You sign in with {{ method() }}, so your password is managed there.</p>
        }
        <p class="muted small">Changes to how we may contact you are recorded with the date, as before. <a routerLink="/projects">My projects</a></p>
      </section>
    </div>
  `,
  styles: `
    .narrow { max-width: 44rem; }
    .facts { display: grid; grid-template-columns: max-content 1fr; gap: .55rem 1.5rem; margin: 0 0 1.25rem; }
    .facts dt { color: var(--bp-ink-muted); }
    .facts dd { margin: 0; font-weight: 600; }
    @media (max-width: 560px) { .facts { grid-template-columns: 1fr; } .facts dd { margin-bottom: .5rem; } }
    .actions { display: flex; gap: .6rem; flex-wrap: wrap; }
    .small { font-size: .85rem; }
  `,
})
export class AccountPage {
  private readonly oidc = inject(OidcSecurityService);

  protected readonly p = computed(() => (this.oidc.userData().userData ?? {}) as ProfileClaims);
  protected readonly address = computed(() => {
    const c = this.p();
    return [c.address_line1, c.address_line2, [c.city, c.region].filter(Boolean).join(', '), c.postal_code].filter(Boolean).join(' · ');
  });
  protected readonly consent = computed(() => CONSENT[this.p().contact_consent ?? ''] ?? 'Not set');
  protected readonly hours = computed(() => HOURS[this.p().contact_hours ?? ''] ?? 'Not set');
  protected readonly hasPassword = computed(() => !this.p().identity_provider);
  protected readonly method = computed(() => {
    const idp = this.p().identity_provider;
    return idp ? idp.charAt(0).toUpperCase() + idp.slice(1) : 'Email and password';
  });

  /** Keycloak's profile form (same fields as sign-up), then back here with fresh claims. */
  protected editProfile(): void {
    this.oidc.authorize(undefined, { customParams: { kc_action: 'UPDATE_PROFILE' } });
  }

  /** Keycloak asks for the current password, then the new one twice. */
  protected changePassword(): void {
    this.oidc.authorize(undefined, { customParams: { kc_action: 'UPDATE_PASSWORD' } });
  }
}
