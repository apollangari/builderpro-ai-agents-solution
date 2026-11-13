import { Component, computed, effect, inject, signal } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { ThemeService } from './core/theme/theme.service';
import { PendingSignup } from './features/signup/pending-signup';
import { SelectedStore } from './core/tenant/selected-store';
import { HostStore } from './core/tenant/host-store';
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

        @if (!host.active && stores.value().length > 1) {
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
          <!-- Account menu: the customer's own things live behind the avatar (owner, 2n-f). -->
          <div class="account" (keydown.escape)="menuOpen.set(false)">
            <button type="button" class="avatar" [attr.aria-expanded]="menuOpen()" aria-haspopup="true" aria-controls="account-menu"
                    (click)="menuOpen.set(!menuOpen())" data-testid="account-button">
              <span aria-hidden="true">{{ initials() }}</span>
              <span class="sr-only">Account menu for </span><span class="sr-only" data-testid="user-name">{{ displayName() }}</span>
            </button>
            @if (menuOpen()) {
              <div class="menu card" id="account-menu" role="menu" data-testid="account-menu" (click)="menuOpen.set(false)">
                <p class="who">{{ displayName() }}</p>
                <a role="menuitem" routerLink="/projects" data-testid="menu-projects">My projects</a>
                <a role="menuitem" routerLink="/quotes">My quotes</a>
                <a role="menuitem" routerLink="/account" data-testid="menu-account">Account</a>
                <button type="button" role="menuitem" class="signout" (click)="signOut()" data-testid="menu-signout">Sign out</button>
              </div>
            }
          </div>
        } @else {
          <button type="button" class="btn btn-primary" (click)="signIn()" data-testid="sign-in">Sign in</button>
        }
      </div>

      <nav class="row nav" aria-label="Main">
        <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Home</a>
        <a routerLink="/quote/new" routerLinkActive="active" data-testid="nav-start">Start a project</a>
        <a routerLink="/catalog" routerLinkActive="active">Catalog</a>
        <a routerLink="/assistant" routerLinkActive="active">Concierge</a>
        <a routerLink="/quote" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }" data-testid="nav-quote">Quote@if (draft.count()) { <span class="count">{{ draft.count() }}</span> }</a>
      </nav>
    </header>
    @if (notMemberHere(); as store) {
      <div class="warning host-unknown" role="alert" data-testid="host-not-member">
        <span>You're signed in, but you're not a customer of {{ store.name }} yet.
          @if (store.signupEnabled) { <a routerLink="/signup">Join {{ store.name }}</a> }
        </span>
      </div>
    }
    @if (host.unknown()) {
      <div class="warning host-unknown" role="alert" data-testid="host-unknown">
        <span>There's no store at this address. Check the link your retailer sent you.</span>
      </div>
    }

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
      width: 44px; height: 44px; border-radius: 50%; border: 0; cursor: pointer;
      background: var(--bp-accent-soft); color: var(--bp-accent);
      font-family: var(--bp-font-heading); font-weight: 600; font-size: .9rem;
    }
    .account { position: relative; }
    .menu {
      position: absolute; right: 0; top: calc(100% + .4rem); z-index: 20; min-width: 13rem;
      display: grid; gap: .1rem; padding: .4rem;
    }
    .menu .who { margin: .2rem .6rem .4rem; font-weight: 600; color: var(--bp-ink-muted); font-size: .85rem; }
    .menu a, .menu .signout {
      display: flex; align-items: center; min-height: var(--bp-tap); padding: 0 .75rem; border-radius: var(--bp-radius-md);
      color: var(--bp-ink); text-decoration: none; background: none; border: 0; font: inherit; text-align: left; cursor: pointer;
    }
    .menu a:hover, .menu .signout:hover { background: var(--bp-surface-muted); }
    .menu .signout { border-top: 1px solid var(--bp-border); border-radius: 0 0 var(--bp-radius-md) var(--bp-radius-md); }

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
  /** 1g: on a store's subdomain (acme.…) the store comes from the address and the picker is hidden. */
  protected readonly host = inject(HostStore);
  protected readonly menuOpen = signal(false);
  /** On a store's address while signed in to an account that isn't one of its customers: say so, offer to join. */
  protected readonly notMemberHere = computed(() => {
    if (!this.host.info.hasValue() || !this.isAuthenticated() || this.stores.status() !== 'resolved') return null;
    const store = this.host.info.value()!;
    return this.stores.value().some((s) => s.tenantId === store.tenantId) ? null : store;
  });
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
      // On a store's subdomain the address decides; the server refuses it if you aren't a member.
      if (this.host.active) return;
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
