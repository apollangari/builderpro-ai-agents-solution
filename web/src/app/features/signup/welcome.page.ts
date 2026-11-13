import { Component, effect, inject } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { describeError } from '../../core/api/describe-error';
import { PendingSignup } from './pending-signup';

/** POST /api/identity/onboarding answer. Mirrors JoinStoreResponse in C#. */
export interface JoinStoreResponse {
  status: 'joined' | 'already_member';
  storeName: string;
  tenantId: string;
  customerId: string;
}

/**
 * Finishes a store sign-up after the visitor is signed in: joins the store they came from.
 * Signal-first: the POST is an httpResource that fires once, because the store is read once.
 */
@Component({
  selector: 'bp-welcome-page',
  imports: [RouterLink],
  template: `
    <div class="page">
      <section class="card box">
        @if (!store) {
          <h1>You're all set</h1>
          <p data-testid="welcome-nothing">There's no store sign-up to finish.</p>
          <a routerLink="/" class="btn">Go to home</a>
        } @else if (join.isLoading()) {
          <h1>Setting up your account…</h1>
          <p data-testid="welcome-loading">Connecting you to the store.</p>
        } @else if (join.error()) {
          <h1>We couldn't finish your sign-up</h1>
          <div class="warning" role="alert" data-testid="welcome-error">
            <span>{{ describe(join.error()) }}</span>
          </div>
        } @else if (join.hasValue()) {
          @let j = join.value();
          <span class="eyebrow">{{ j.status === 'joined' ? 'Welcome' : 'Welcome back' }}</span>
          <h1 data-testid="welcome-store">{{ j.storeName }}</h1>
          <p>
            @if (j.status === 'joined') {
              Your account is ready. You can browse the catalog and request quotes.
            } @else {
              You're already a customer of this store.
            }
          </p>
          <a routerLink="/catalog" class="btn btn-primary">Browse the catalog</a>
        }
      </section>
    </div>
  `,
  styles: `
    .page { display: grid; place-items: center; min-height: calc(100dvh - 10rem); }
    .box { width: min(30rem, 100%); text-align: center; }
    h1 { font-size: 1.6rem; margin-bottom: .6rem; }
    p { color: var(--bp-ink-muted); margin: 0 0 1.25rem; }
    .warning { text-align: left; }
  `,
})
export class WelcomePage {
  private readonly pending = inject(PendingSignup);
  protected readonly describe = describeError;

  /** Read once: the request below must not re-fire when storage is cleared. */
  protected readonly store = this.pending.get();

  protected readonly join = httpResource<JoinStoreResponse>(() =>
    this.store ? { url: '/api/identity/onboarding', method: 'POST', body: { store: this.store } } : undefined,
  );

  constructor() {
    // Done (or refused for good): forget the pending store so the shell stops sending us here.
    effect(() => {
      if (this.join.hasValue() || this.join.error()) this.pending.clear();
    });
  }
}
