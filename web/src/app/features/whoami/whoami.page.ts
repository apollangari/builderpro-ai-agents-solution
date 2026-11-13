import { Component, computed } from '@angular/core';
import { DatePipe } from '@angular/common';
import { describeError } from '../../core/api/describe-error';
import { meResource, platformMeResource } from './whoami.api';

type Verdict = { tone: 'ok' | 'warning'; label: string; text: string };

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
    <div class="page">
      <header class="page-head head-row">
        <div>
          <span class="eyebrow">Identity</span>
          <h1>Who am I?</h1>
          <p>The same person, seen twice: once by your token, once by the platform. When they disagree, the platform wins.</p>
        </div>
        <button type="button" class="btn" (click)="reload()" [disabled]="busy()">Refresh both</button>
      </header>

      @if (verdict(); as v) {
        <div class="verdict" [class.warning]="v.tone === 'warning'" data-testid="verdict">
          <span class="badge" [class.badge-accent]="v.tone === 'ok'" [class.badge-warning]="v.tone === 'warning'">{{ v.label }}</span>
          <span>{{ v.text }}</span>
        </div>
      }

      <div class="grid">
        <section class="card">
          <h2 class="card-title">Token view</h2>
          <p class="card-sub">What your access token says · <code>/api/me</code> → me-api</p>

          @if (token.isLoading()) {
            <div class="loading" data-testid="token-loading" aria-label="Loading">
              @for (w of skeleton; track $index) {
                <span class="skeleton" [style.width.%]="w"></span>
              }
            </div>
          } @else if (token.error()) {
            <div class="warning" role="alert" data-testid="token-error">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M12 3 2 20h20L12 3Z" /><path d="M12 10v4m0 3h.01" /></svg>
              <span>{{ describe(token.error()) }}</span>
            </div>
          } @else if (token.hasValue()) {
            @let me = token.value();
            <dl class="facts" data-testid="me">
              <dt>User</dt>
              <dd>{{ me.userName ?? '—' }}</dd>
              <dt>sub</dt>
              <dd class="mono">{{ me.userId ?? '—' }}</dd>
              <dt>Tenant claim</dt>
              <dd data-testid="tenant">
                @if (me.tenantId) {
                  <span class="badge">{{ me.tenantId }}</span>
                } @else {
                  <span class="muted">— (none)</span>
                }
              </dd>
              <dt>Roles</dt>
              <dd>
                <span class="chips">
                  @for (r of me.roles; track r) {
                    <span class="badge">{{ r }}</span>
                  } @empty {
                    <span class="muted">—</span>
                  }
                </span>
              </dd>
              <dt>Answered</dt>
              <dd class="muted">{{ me.service }} · {{ me.serverTimeUtc | date: 'mediumTime' }}</dd>
            </dl>
          }
        </section>

        <section class="card">
          <h2 class="card-title">Platform view</h2>
          <p class="card-sub">What the platform grants · <code>/api/identity/me</code> → identity</p>

          @if (platform.isLoading()) {
            <div class="loading" data-testid="platform-loading" aria-label="Loading">
              @for (w of skeleton; track $index) {
                <span class="skeleton" [style.width.%]="w"></span>
              }
            </div>
          } @else if (platform.error()) {
            <div class="warning" role="alert" data-testid="platform-error">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M12 3 2 20h20L12 3Z" /><path d="M12 10v4m0 3h.01" /></svg>
              <span>{{ describe(platform.error()) }}</span>
            </div>
          } @else if (platform.hasValue()) {
            @let p = platform.value();
            <dl class="facts" data-testid="platform">
              <dt>Tenant</dt>
              <dd data-testid="platform-tenant"><span class="badge badge-accent">{{ p.tenantId }}</span></dd>
              <dt>Customer</dt>
              <dd>{{ p.customerId ?? '—' }}</dd>
              <dt>Scopes</dt>
              <dd>
                <span class="chips">
                  @for (sc of p.scopes; track sc) {
                    <span class="badge">{{ sc }}</span>
                  } @empty {
                    <span class="muted">—</span>
                  }
                </span>
              </dd>
              <dt>User key</dt>
              <dd class="mono">{{ p.userKey }}</dd>
              <dt>Source</dt>
              <dd>{{ p.source }}</dd>
            </dl>
          }
        </section>
      </div>
    </div>
  `,
  styles: `
    .head-row { display: flex; align-items: flex-end; justify-content: space-between; gap: 1rem; flex-wrap: wrap; }

    .verdict {
      display: flex; align-items: center; gap: .75rem; flex-wrap: wrap;
      padding: .75rem 1rem; margin-bottom: 1rem; border-radius: var(--bp-radius-md);
      background: var(--bp-surface); border: 1px solid var(--bp-border); font-size: .92rem;
    }
    .verdict.warning { background: var(--bp-warning-bg); color: var(--bp-warning-ink); border-color: var(--bp-warning-bg); }

    .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 1.25rem; }
    @media (max-width: 900px) { .grid { grid-template-columns: 1fr; } }

    .loading { display: grid; gap: .8rem; padding: .25rem 0; }
    .muted { color: var(--bp-ink-muted); font-weight: 400; }
    .mono { font-size: .75rem; font-weight: 500; }
  `,
})
export class WhoAmIPage {
  // Requests start when the page is created (reached only when signed in: route guard).
  protected readonly token = meResource();
  protected readonly platform = platformMeResource();
  protected readonly describe = describeError;
  protected readonly skeleton = [70, 45, 85, 55, 60];

  protected readonly busy = computed(() => this.token.isLoading() || this.platform.isLoading());

  /** One sentence comparing the two views, once both have answered. */
  protected readonly verdict = computed<Verdict | null>(() => {
    if (this.busy()) return null;
    if (this.platform.error()) {
      return { tone: 'warning', label: 'No access', text: 'The platform did not grant you a tenant, whatever your token says.' };
    }
    if (!this.token.hasValue() || !this.platform.hasValue()) return null;

    const claim = this.token.value().tenantId;
    const granted = this.platform.value().tenantId;
    if (!claim) {
      return { tone: 'ok', label: 'Resolved', text: `No tenant claim in your token; the platform resolved ${granted} from your membership.` };
    }
    return claim === granted
      ? { tone: 'ok', label: 'In agreement', text: `Token and platform agree: you're acting for ${granted}.` }
      : { tone: 'warning', label: 'Platform wins', text: `Your token says ${claim}, but the platform grants ${granted}.` };
  });

  reload(): void {
    this.token.reload();
    this.platform.reload();
  }
}
