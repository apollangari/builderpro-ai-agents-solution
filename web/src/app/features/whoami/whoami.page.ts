import { Component } from '@angular/core';
import { DatePipe } from '@angular/common';
import { describeError, meResource, platformMeResource } from './whoami.service';

/**
 * Two views of the same person, side by side, on purpose:
 *  - TOKEN view (Me API): what the identity provider put in your access token.
 *  - PLATFORM view (Identity service): the tenant the platform actually grants, from memberships.
 * When they disagree, the platform wins. That's the lesson of increment 1d.
 *
 * Signal-first: each card is an httpResource. Two independent resources, so one failing
 * never hides the other's answer.
 */
@Component({
  selector: 'bp-whoami-page',
  imports: [DatePipe],
  template: `
    <div class="grid">
      <section class="card">
        <h2>Token view</h2>
        <p class="sub">What your access token says · <code>/api/me</code> → me-api</p>
        @if (token.isLoading()) {
          <p data-testid="token-loading">Loading…</p>
        } @else if (token.error()) {
          <p class="error" data-testid="token-error">{{ describe(token.error()) }}</p>
        } @else if (token.hasValue()) {
          @let me = token.value();
          <dl data-testid="me">
            <dt>User</dt><dd>{{ me.userName ?? '—' }}</dd>
            <dt>sub</dt><dd class="mono">{{ me.userId ?? '—' }}</dd>
            <dt>Tenant claim</dt><dd data-testid="tenant">{{ me.tenantId ?? '— (none)' }}</dd>
            <dt>Roles</dt><dd>{{ me.roles.join(', ') || '—' }}</dd>
            <dt>Answered</dt><dd>{{ me.service }} · {{ me.serverTimeUtc | date: 'mediumTime' }}</dd>
          </dl>
        }
      </section>

      <section class="card">
        <h2>Platform view</h2>
        <p class="sub">What the platform grants · <code>/api/identity/me</code> → identity</p>
        @if (platform.isLoading()) {
          <p data-testid="platform-loading">Loading…</p>
        } @else if (platform.error()) {
          <p class="error" data-testid="platform-error">{{ describe(platform.error()) }}</p>
        } @else if (platform.hasValue()) {
          @let p = platform.value();
          <dl data-testid="platform">
            <dt>Tenant</dt><dd data-testid="platform-tenant">{{ p.tenantId }}</dd>
            <dt>Customer</dt><dd>{{ p.customerId ?? '—' }}</dd>
            <dt>Scopes</dt><dd>{{ p.scopes.join(', ') || '—' }}</dd>
            <dt>User key</dt><dd class="mono">{{ p.userKey }}</dd>
            <dt>Source</dt><dd>{{ p.source }}</dd>
          </dl>
        }
      </section>
    </div>
    <p class="center"><button type="button" (click)="reload()">Refresh both</button></p>
  `,
  styles: `
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(20rem, 1fr)); gap: 1.5rem; max-width: 60rem; margin: 2rem auto; padding: 0 1rem; font-family: system-ui, sans-serif; }
    .card { padding: 1.25rem 1.5rem; border: 1px solid #d0d7de; border-radius: 12px; }
    h2 { margin: 0; font-size: 1.15rem; }
    .sub { margin: .25rem 0 1rem; color: #57606a; font-size: .85rem; }
    dl { display: grid; grid-template-columns: 7rem 1fr; gap: .4rem 1rem; margin: 0; }
    dt { color: #57606a; } dd { margin: 0; font-weight: 600; }
    .mono { font-family: ui-monospace, monospace; font-size: .75rem; word-break: break-all; }
    .error { color: #b42318; }
    .center { text-align: center; font-family: system-ui, sans-serif; }
    button { padding: .5rem 1rem; border-radius: 8px; border: 1px solid #d0d7de; background: #f6f8fa; cursor: pointer; }
  `,
})
export class WhoAmIPage {
  // Requests start when the page is created (reached only when signed in: route guard).
  protected readonly token = meResource();
  protected readonly platform = platformMeResource();
  protected readonly describe = describeError;

  reload(): void {
    this.token.reload();
    this.platform.reload();
  }
}
