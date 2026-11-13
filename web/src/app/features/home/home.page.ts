import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { StoreFeatures } from '../../core/tenant/store-features';

/** One centred card: what BuilderPro is, and the next step for this visitor. */
@Component({
  selector: 'bp-home-page',
  imports: [RouterLink],
  template: `
    <div class="page">
      <section class="card home">
        <span class="eyebrow">Home improvement, priced for your address</span>
        <h1>What would you like to do?</h1>
        <p class="lede">Choose, measure and get a quote step by step, or tell our concierge about your project.</p>

        <!-- One flow (owner): no top menu. The store's package (1h) decides which of the two it offers. -->
        @if (isAuthenticated()) {
          <div class="actions" data-testid="home-signed-in">
            <a routerLink="/quote/new" class="btn btn-primary" data-testid="start-guided">Start a project</a>
            @if (features.concierge()) {
              <a routerLink="/assistant" class="btn" data-testid="start-concierge">Plan it with the concierge</a>
            }
          </div>
          <p class="hint"><a routerLink="/projects">Continue a project</a></p>
        } @else {
          <div class="actions" data-testid="home-signed-out">
            <a routerLink="/quote/new" class="btn btn-primary">Sign in to start a project</a>
          </div>
          <p class="hint">
            This page is public; the others ask you to sign in. Local test users:
            <code>alice</code>, <code>bob</code>, <code>carol</code>, password <code>Passw0rd!</code>
          </p>
        }
      </section>
    </div>
  `,
  styles: `
    .page { display: grid; place-items: center; min-height: calc(100dvh - 10rem); }
    .home { width: min(36rem, 100%); text-align: center; padding: 2.25rem 2rem; }
    h1 { font-size: clamp(1.6rem, 4vw, 2.1rem); }
    .lede { margin: .9rem auto 1.6rem; color: var(--bp-ink-muted); max-width: 30rem; }
    .actions { display: flex; flex-wrap: wrap; justify-content: center; gap: .75rem; }
    .hint { margin: 1.25rem 0 0; font-size: .85rem; color: var(--bp-ink-muted); }
  `,
})
export class HomePage {
  private readonly oidc = inject(OidcSecurityService);
  protected readonly isAuthenticated = computed(() => this.oidc.authenticated().isAuthenticated);
  /** The store's package (1h): the concierge path only when it's included. */
  protected readonly features = inject(StoreFeatures);
}
