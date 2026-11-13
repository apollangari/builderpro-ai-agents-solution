import { Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';

/** The shell: a header with sign-in state, and the routed page below. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header class="bar">
      <a routerLink="/" class="brand">BuilderPro</a>
      <nav>
        <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Home</a>
        <a routerLink="/me" routerLinkActive="active">Who am I</a>
      </nav>
      <span class="spacer"></span>
      @if (isAuthenticated()) {
        <span class="user" data-testid="user-name">{{ displayName() }}</span>
        <button type="button" (click)="signOut()">Sign out</button>
      } @else {
        <button type="button" (click)="signIn()" data-testid="sign-in">Sign in</button>
      }
    </header>
    <router-outlet />
  `,
  styles: `
    .bar { display: flex; align-items: center; gap: 1rem; padding: .75rem 1.5rem; border-bottom: 1px solid #d0d7de; font-family: system-ui, sans-serif; }
    .brand { font-weight: 700; text-decoration: none; color: inherit; }
    nav a { margin-right: .75rem; color: #57606a; text-decoration: none; }
    nav a.active { color: #0969da; font-weight: 600; }
    .spacer { flex: 1; }
    .user { color: #57606a; }
    button { padding: .4rem .9rem; border-radius: 8px; border: 1px solid #d0d7de; background: #f6f8fa; cursor: pointer; }
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

  signIn(): void {
    this.oidc.authorize();
  }

  signOut(): void {
    this.oidc.logoff().subscribe();
  }
}
