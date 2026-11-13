import { Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';

@Component({
  selector: 'bp-home-page',
  imports: [RouterLink],
  template: `
    <section class="card">
      <h1>Welcome to BuilderPro</h1>
      @if (isAuthenticated()) {
        <p data-testid="home-signed-in">You're signed in. See <a routerLink="/me">who you are</a> as the API sees you.</p>
      } @else {
        <p data-testid="home-signed-out">
          This page is public. <a routerLink="/me">Who am I</a> is protected: opening it sends you to sign in.
        </p>
        <p class="hint">Test users (local Keycloak): <code>alice</code> / <code>bob</code> / <code>carol</code>, password <code>Passw0rd!</code></p>
      }
    </section>
  `,
  styles: `
    .card { max-width: 32rem; margin: 3rem auto; padding: 1.5rem 2rem; border: 1px solid #d0d7de; border-radius: 12px; font-family: system-ui, sans-serif; }
    .hint { color: #57606a; font-size: .9rem; }
  `,
})
export class HomePage {
  private readonly oidc = inject(OidcSecurityService);
  protected readonly isAuthenticated = computed(() => this.oidc.authenticated().isAuthenticated);
}
