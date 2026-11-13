import { Component, computed, effect, inject } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { ThemeService } from './core/theme/theme.service';
import { PendingSignup } from './features/signup/pending-signup';
import { SelectedStore } from './core/tenant/selected-store';
import { QuoteDraft } from './features/quote/quote-draft';

/** GET /api/identity/memberships/mine. Mirrors MyMembershipDto in C#. */
interface MyMembership {
  tenantId: string;
  storeName: string;
  customerId: string | null;
}

/**
 * The shell, "Option A: Studio split" header:
 *   left  = retailer mark + name, "powered by BuilderPro"
 *   right = project label, sign-in/out, avatar
 * Navigation sits on a second row so every link keeps a 44 px target on small screens.
 */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header class="bar">
      <div class="row">
        <a routerLink="/" class="brand" aria-label="Home">
          <span class="mark" aria-hidden="true">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <path d="M3 11 12 4l9 7" /><path d="M5 10v10h14V10" /><path d="M10 20v-5h4v5" />
            </svg>
          </span>
          <span class="names">
            <span class="retailer" data-testid="retailer-name">{{ theme.retailerName() }}</span>
            <span class="powered">powered by BuilderPro</span>
          </span>
        </a>

        <span class="spacer"></span>

        @if (stores.value().length > 1) {
          <label class="picker">
            <span class="sr-only">Store</span>
            <select data-testid="store-picker" [value]="selected.tenantId() ?? ''" (change)="pick($any($event.target).value)">
              <option value="" disabled>Choose a store…</option>
              @for (s of stores.value(); track s.tenantId) {
                <option [value]="s.tenantId">{{ s.storeName }}</option>
              }
            </select>
          </label>
        }

        <span class="project" data-testid="project-label">{{ theme.projectLabel() }}</span>

        @if (isAuthenticated()) {
          <button type="button" class="btn" (click)="signOut()">Sign out</button>
          <span class="avatar" [attr.title]="displayName()" aria-hidden="true">{{ initials() }}</span>
          <span class="sr-only" data-testid="user-name">{{ displayName() }}</span>
        } @else {
          <button type="button" class="btn btn-primary" (click)="signIn()" data-testid="sign-in">Sign in</button>
        }
      </div>

      <nav class="row nav" aria-label="Main">
        <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Home</a>
        <a routerLink="/quote/new" routerLinkActive="active" data-testid="nav-start">Start a project</a>
        <a routerLink="/catalog" routerLinkActive="active">Catalog</a>
        <a routerLink="/assistant" routerLinkActive="active">Assistant</a>
        <a routerLink="/quote" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }" data-testid="nav-quote">Quote@if (draft.count()) { <span class="count">{{ draft.count() }}</span> }</a>
        <a routerLink="/quotes" routerLinkActive="active">My quotes</a>
        <a routerLink="/me" routerLinkActive="active">Who am I</a>
      </nav>
    </header>

    <main>
      <router-outlet />
    </main>
  `,
  styles: `
    :host { display: flex; flex-direction: column; min-height: 100dvh; }
    main { flex: 1; }

    .bar { background: var(--bp-surface); border-bottom: 1px solid var(--bp-border); }
    .row {
      max-width: var(--bp-page-max); margin: 0 auto; padding: .75rem 1.25rem;
      display: flex; align-items: center; gap: .9rem;
    }

    .brand { display: inline-flex; align-items: center; gap: .7rem; min-height: var(--bp-tap); text-decoration: none; color: var(--bp-ink); }
    .brand:hover { color: var(--bp-ink); }
    .mark {
      display: grid; place-items: center; flex: none;
      width: 32px; height: 32px; border-radius: var(--bp-radius-sm);
      background: var(--bp-accent); color: var(--bp-on-accent);
    }
    .mark svg { width: 18px; height: 18px; }
    .names { display: flex; flex-direction: column; line-height: 1.2; }
    .retailer { font-family: var(--bp-font-heading); font-weight: 700; font-size: 1.05rem; }
    .powered { font-size: 12px; color: var(--bp-ink-muted); }

    .spacer { flex: 1; }
    .project { color: var(--bp-ink-muted); font-size: .9rem; }

    .avatar {
      display: grid; place-items: center; flex: none;
      width: 44px; height: 44px; border-radius: 50%;
      background: var(--bp-accent-soft); color: var(--bp-accent);
      font-family: var(--bp-font-heading); font-weight: 600; font-size: .9rem;
    }

    .picker select {
      min-height: var(--bp-tap); padding: 0 .75rem; border-radius: var(--bp-radius-md);
      border: 1px solid var(--bp-border-strong); background: var(--bp-surface); color: var(--bp-ink);
      font: inherit; font-weight: 600;
    }

    .nav { padding-top: 0; padding-bottom: .5rem; gap: .25rem; }
    .nav a {
      display: inline-flex; align-items: center; min-height: var(--bp-tap); padding: 0 .9rem;
      border-radius: var(--bp-radius-md); text-decoration: none;
      color: var(--bp-ink-muted); font-weight: 500;
    }
    .nav a:hover { color: var(--bp-ink); background: var(--bp-surface-muted); }
    .nav a.active { color: var(--bp-accent); background: var(--bp-accent-soft); font-weight: 600; }
    .nav .count { margin-left: .35rem; padding: 0 .45rem; border-radius: 999px; background: var(--bp-accent); color: var(--bp-on-accent); font-size: .75rem; font-weight: 700; }
    .nav { overflow-x: auto; }

    @media (max-width: 640px) {
      .row { gap: .5rem; padding-left: 1rem; padding-right: 1rem; }
      .project { display: none; }
      .powered { display: none; }
    }
  `,
})
export class App {
  private readonly oidc = inject(OidcSecurityService);
  protected readonly theme = inject(ThemeService);

  private readonly router = inject(Router);
  private readonly pendingSignup = inject(PendingSignup);

  /**
   * Once per sign-in: let Identity record the contact consent in this token if it changed (1f audit
   * trail). Fire-and-forget; nothing on screen depends on it, so errors are ignored here.
   */
  protected readonly consentSync = httpResource(() =>
    this.isAuthenticated() ? { url: '/api/identity/consent/sync', method: 'POST' } : undefined,
  );

  protected readonly selected = inject(SelectedStore);
  protected readonly draft = inject(QuoteDraft);

  /** The stores this user belongs to; the picker shows only when there are two or more. */
  protected readonly stores = httpResource<MyMembership[]>(
    () => (this.isAuthenticated() ? '/api/identity/memberships/mine' : undefined),
    { defaultValue: [] },
  );

  /** Switch store: every page's data depends on it, so start the page fresh. */
  pick(tenantId: string): void {
    this.selected.select(tenantId);
    window.location.reload();
  }

  constructor() {
    // A remembered pick that is no longer one of your stores is dropped.
    effect(() => {
      const mine = this.stores.value();
      const pick = this.selected.tenantId();
      // status 'resolved', not hasValue(): with defaultValue [] hasValue() is already true before
      // the list arrives, which would wrongly drop the pick on every page load.
      if (this.stores.status() === 'resolved' && pick && !mine.some((s) => s.tenantId === pick)) this.selected.select(null);
    });

    // Static brand for now; later the tenant's config is passed here.
    this.theme.apply();

    // Back from Keycloak after using a store's sign-up link: finish joining that store.
    effect(() => {
      if (this.isAuthenticated() && this.pendingSignup.get() && !this.router.url.startsWith('/welcome')) {
        void this.router.navigate(['/welcome']);
      }
    });
  }

  protected readonly isAuthenticated = computed(() => this.oidc.authenticated().isAuthenticated);
  // userData = the ID token / userinfo claims. Display only: authorization happens on the server.
  protected readonly displayName = computed(() => {
    const user = this.oidc.userData().userData as { name?: string; preferred_username?: string } | null;
    return user?.name ?? user?.preferred_username ?? '';
  });
  protected readonly initials = computed(() =>
    this.displayName()
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((part) => part[0]!.toUpperCase())
      .join(''),
  );

  signIn(): void {
    this.oidc.authorize();
  }

  signOut(): void {
    // logoff() returns an Observable: one of the few places RxJS remains (library API).
    this.oidc.logoff().subscribe();
  }
}
