import { Component, OnInit, computed, inject, input } from '@angular/core';
import { HostStore } from '../../core/tenant/host-store';
import { Router, RouterLink } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { PendingSignup } from './pending-signup';

/**
 * The store's sign-up link: /signup?store=acme (decision 1e).
 * Remembers the store, then opens Keycloak's "Create your account" page directly (prompt=create).
 * Already signed in? Skip straight to /welcome to join the store.
 */
@Component({
  selector: 'bp-signup-page',
  imports: [RouterLink],
  template: `
    <div class="page">
      <section class="card box">
        @if (valid()) {
          <h1>Creating your account…</h1>
          <p data-testid="signup-redirecting">Taking you to the sign-up page.</p>
        } @else {
          <h1>Sign-up link needed</h1>
          <div class="warning" role="alert" data-testid="signup-invalid">
            <span>This link doesn't say which store you're signing up with. Ask your retailer for their sign-up link.</span>
          </div>
          <div class="page-actions"><a routerLink="/" class="btn">Back to home</a></div>
        }
      </section>
    </div>
  `,
  styles: `
    .page { display: grid; place-items: center; min-height: calc(100dvh - 10rem); }
    .box { width: min(30rem, 100%); text-align: center; }
    h1 { font-size: 1.5rem; margin-bottom: .75rem; }
    p { color: var(--bp-ink-muted); }
    .warning { text-align: left; margin-bottom: 1rem; }
  `,
})
export class SignupPage implements OnInit {
  /** Bound from the query string by withComponentInputBinding(). */
  readonly store = input<string | undefined>();

  private readonly oidc = inject(OidcSecurityService);
  private readonly router = inject(Router);
  private readonly pending = inject(PendingSignup);

  /** ?store=acme, or on a store's own address (acme.…) the store from the subdomain (1g). */
  private readonly host = inject(HostStore);
  private readonly storeId = computed(() => this.store() ?? this.host.slug ?? undefined);
  protected readonly valid = computed(() => PendingSignup.isValidStoreId(this.storeId()));

  ngOnInit(): void {
    const store = this.storeId();
    if (!PendingSignup.isValidStoreId(store)) return;

    this.pending.set(store);
    if (this.oidc.authenticated().isAuthenticated) {
      void this.router.navigate(['/welcome']);
    } else {
      // prompt=create (OIDC "initiating user registration"): Keycloak opens Create account, not Sign in.
      this.oidc.authorize(undefined, { customParams: { prompt: 'create' } });
    }
  }
}
