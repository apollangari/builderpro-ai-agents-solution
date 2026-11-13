import { Component, computed } from '@angular/core';
import { DatePipe } from '@angular/common';
import { describeError } from '../../core/api/describe-error';
import { meResource, platformMeResource } from './whoami.api';

type Verdict = { tone: 'ok' | 'warn' | 'danger'; label: string; text: string };

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
          <span class="eyebrow">Identity · increment 1d</span>
          <h1>Who am I?</h1>
          <p>
            The same person, seen twice: once by your <strong>token</strong>, once by the
            <strong>platform</strong>. When they disagree, the platform wins.
          </p>
        </div>
        <button type="button" class="btn" (click)="reload()" [disabled]="busy()">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" [class.spin]="busy()">
            <path d="M20 12a8 8 0 1 1-2.3-5.6M20 4v5h-5" />
          </svg>
          Refresh both
        </button>
      </header>

      @if (verdict(); as v) {
        <div class="verdict" [class.ok]="v.tone === 'ok'" [class.warn]="v.tone === 'warn'" [class.danger]="v.tone === 'danger'" data-testid="verdict">
          <span class="badge" [class.badge-ok]="v.tone === 'ok'" [class.badge-warn]="v.tone === 'warn'" [class.badge-danger]="v.tone === 'danger'">{{ v.label }}</span>
          <span>{{ v.text }}</span>
        </div>
      }

      <div class="grid">
        <section class="card">
          <div class="card-head">
            <span class="icon-tile">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
                <rect x="3" y="5" width="18" height="14" rx="2" /><circle cx="9" cy="12" r="2.5" /><path d="M14 10h4M14 14h3" />
              </svg>
            </span>
            <div>
              <h2>Token view</h2>
              <p>What your access token says · <code>/api/me</code> → me-api</p>
            </div>
          </div>

          @if (token.isLoading()) {
            <div class="loading" data-testid="token-loading" aria-label="Loading">
              @for (w of skeleton; track $index) {
                <span class="skeleton" [style.width.%]="w"></span>
              }
            </div>
          } @else if (token.error()) {
            <div class="alert" data-testid="token-error">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9" /><path d="M12 8v5m0 3h.01" /></svg>
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
                  <span class="badge badge-info">{{ me.tenantId }}</span>
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

        <section class="card platform-card">
          <div class="card-head">
            <span class="icon-tile">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
                <path d="M12 3 4 6v6c0 4.5 3.4 8.3 8 9 4.6-.7 8-4.5 8-9V6l-8-3Z" /><path d="m9 12 2 2 4-4" />
              </svg>
            </span>
            <div>
              <h2>Platform view</h2>
              <p>What the platform grants · <code>/api/identity/me</code> → identity</p>
            </div>
          </div>

          @if (platform.isLoading()) {
            <div class="loading" data-testid="platform-loading" aria-label="Loading">
              @for (w of skeleton; track $index) {
                <span class="skeleton" [style.width.%]="w"></span>
              }
            </div>
          } @else if (platform.error()) {
            <div class="alert" data-testid="platform-error">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9" /><path d="M12 8v5m0 3h.01" /></svg>
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
              <dd><span class="badge badge-ok">{{ p.source }}</span></dd>
            </dl>
          }
        </section>
      </div>
    </div>
  `,
  styles: `
    .head-row { display: flex; align-items: flex-end; justify-content: space-between; gap: 1.5rem; flex-wrap: wrap; }
    h1 { font-size: clamp(1.8rem, 3.5vw, 2.4rem); }
    .btn[disabled] { opacity: .6; cursor: progress; transform: none; }
    .spin { animation: spin .9s linear infinite; }
    @keyframes spin { to { transform: rotate(360deg); } }

    .verdict {
      display: flex; align-items: center; gap: .75rem; flex-wrap: wrap;
      padding: .8rem 1rem; margin-bottom: 1.25rem; border-radius: var(--radius);
      border: 1px solid var(--border); background: var(--surface); box-shadow: var(--shadow-sm);
      font-size: .92rem;
    }
    .verdict.ok { border-color: color-mix(in srgb, var(--ok) 40%, var(--border)); }
    .verdict.warn { border-color: color-mix(in srgb, var(--warn) 40%, var(--border)); }
    .verdict.danger { border-color: color-mix(in srgb, var(--danger) 40%, var(--border)); }

    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(21rem, 100%), 1fr)); gap: 1.25rem; }
    .platform-card { border-top: 3px solid var(--accent); }
    .loading { display: grid; gap: .8rem; padding: .25rem 0; }
    .muted { color: var(--text-muted); font-weight: 400; }
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
      return { tone: 'danger', label: 'No access', text: 'The platform did not grant you a tenant, whatever your token says.' };
    }
    if (!this.token.hasValue() || !this.platform.hasValue()) return null;

    const claim = this.token.value().tenantId;
    const granted = this.platform.value().tenantId;
    if (!claim) {
      return { tone: 'ok', label: 'Resolved', text: `No tenant claim in your token; the platform resolved ${granted} from your membership.` };
    }
    return claim === granted
      ? { tone: 'ok', label: 'In agreement', text: `Token and platform agree: you're acting for ${granted}.` }
      : { tone: 'warn', label: 'Platform wins', text: `Your token says ${claim}, but the platform grants ${granted}.` };
  });

  reload(): void {
    this.token.reload();
    this.platform.reload();
  }
}
