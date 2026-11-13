import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';

/** One centred card: what BuilderPro is, and the next step for this visitor. */
@Component({
  selector: 'bp-home-page',
  imports: [RouterLink],
  template: `
    <div class="page">
      <section class="card home">
        <span class="eyebrow">Agent-first home improvement</span>
        <h1>Plan the project. Let the agents do the legwork.</h1>
        <p class="lede">
          Agents interpret and propose. Prices, dates and approvals always come from the platform.
        </p>

        @if (isAuthenticated()) {
          <div class="actions" data-testid="home-signed-in">
            <a routerLink="/catalog" class="btn btn-primary">Browse your catalog</a>
            <a routerLink="/me" class="btn">Who am I?</a>
          </div>
          <p class="hint">You're signed in.</p>
        } @else {
          <div class="actions" data-testid="home-signed-out">
            <a routerLink="/me" class="btn btn-primary">Sign in to continue</a>
            <a routerLink="/catalog" class="btn">See the catalog</a>
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
}
