import { Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';

/** The shell: a sticky header with brand, navigation and sign-in state; the routed page below. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header class="bar">
      <div class="bar-inner">
        <a routerLink="/" class="brand" aria-label="BuilderPro home">
          <span class="mark" aria-hidden="true">
            <svg viewBox="0 0 24 24" fill="currentColor"><path d="M3 11.2 12 3l9 8.2V20a1 1 0 0 1-1 1h-5.2v-6.3H9.2V21H4a1 1 0 0 1-1-1z" /></svg>
          </span>
          <span class="word">Builder<span>Pro</span></span>
        </a>

        <nav aria-label="Main">
          <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Home</a>
          <a routerLink="/me" routerLinkActive="active">Who am I</a>
        </nav>

        <span class="spacer"></span>

        @if (isAuthenticated()) {
          <div class="who">
            <span class="avatar" aria-hidden="true">{{ initials() }}</span>
            <span class="user" data-testid="user-name">{{ displayName() }}</span>
          </div>
          <button type="button" class="btn btn-ghost" (click)="signOut()">Sign out</button>
        } @else {
          <button type="button" class="btn btn-primary" (click)="signIn()" data-testid="sign-in">Sign in</button>
        }
      </div>
    </header>

    <main>
      <router-outlet />
    </main>

    <footer class="foot">
      <span>BuilderPro · agents decide, the domain enforces</span>
    </footer>
  `,
  styles: `
    :host { display: flex; flex-direction: column; min-height: 100dvh; }
    main { flex: 1; }

    .bar {
      position: sticky; top: 0; z-index: 10;
      background: color-mix(in srgb, var(--bg) 75%, transparent);
      backdrop-filter: saturate(1.4) blur(14px);
      -webkit-backdrop-filter: saturate(1.4) blur(14px);
      border-bottom: 1px solid var(--border);
    }
    .bar-inner {
      max-width: var(--page-max); margin: 0 auto; padding: .7rem 1.25rem;
      display: flex; align-items: center; gap: 1.25rem;
    }

    .brand { display: inline-flex; align-items: center; gap: .55rem; text-decoration: none; color: var(--text); }
    .mark {
      display: grid; place-items: center; width: 2rem; height: 2rem; border-radius: 10px;
      background: linear-gradient(135deg, var(--accent), var(--accent-strong));
      color: #fff; box-shadow: 0 6px 16px -6px var(--accent);
    }
    .mark svg { width: 1.1rem; height: 1.1rem; }
    .word { font-family: var(--font-display); font-weight: 700; font-size: 1.1rem; letter-spacing: -.02em; }
    .word span { color: var(--accent); }

    nav { display: flex; gap: .25rem; }
    nav a {
      padding: .4rem .8rem; border-radius: 999px; text-decoration: none;
      color: var(--text-muted); font-weight: 500; font-size: .9rem;
      transition: background-color .15s ease, color .15s ease;
    }
    nav a:hover { color: var(--text); background: var(--surface-2); }
    nav a.active { color: var(--accent-strong); background: var(--accent-soft); font-weight: 600; }

    .spacer { flex: 1; }

    .who { display: inline-flex; align-items: center; gap: .55rem; }
    .avatar {
      display: grid; place-items: center; width: 2rem; height: 2rem; border-radius: 50%;
      background: var(--surface-2); border: 1px solid var(--border);
      font-weight: 700; font-size: .75rem; color: var(--accent-strong);
    }
    .user { font-weight: 600; font-size: .9rem; }

    .foot {
      max-width: var(--page-max); width: 100%; margin: 0 auto; padding: 1.5rem 1.25rem 2rem;
      color: var(--text-muted); font-size: .8rem; border-top: 1px solid var(--border);
    }

    @media (max-width: 640px) {
      .bar-inner { gap: .4rem; padding: .6rem 1rem; }
      nav a { padding: .35rem .6rem; }
      .bar .btn { padding: .45rem .75rem; }
      .word { display: none; }
      .user { display: none; }
    }
  `,
})
export class App {
  private readonly oidc = inject(OidcSecurityService);

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
