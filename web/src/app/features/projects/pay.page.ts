import { Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Router } from '@angular/router';
import { describeError } from '../../core/api/describe-error';
import { DepositInfo } from '../quote/guided';

/** Leaves the app for the payment provider's own page (Stripe Checkout). A seam so tests don't navigate. */
export const leaveFor = { go: (url: string) => window.location.assign(url) };

/**
 * The deposit payment page (6a, 6d). The amount always comes from the server, and only the customer pays.
 *
 * Status follows the payment state machine: created -> processing -> succeeded | failed | unknown,
 * where unknown is settled by reconciliation (the provider's webhook or our status lookup).
 *  - simulated provider (development): this page IS the test-mode checkout: Pay, or Decline to try a failure.
 *  - stripe: "Continue to secure payment" opens Stripe Checkout. Coming back (?return=stripe) the page
 *    asks the server to check with Stripe (refresh=true) every couple of seconds until it has an answer.
 */
@Component({
  selector: 'bp-pay-page',
  imports: [CurrencyPipe],
  template: `
    <div class="page">
      <section class="card pay" data-testid="pay-card">
        @if (deposit.error()) {
          <div class="warning" role="alert"><span>{{ describe(deposit.error()) }}</span></div>
        } @else if (current(); as d) {
          <span class="eyebrow">{{ d.kind === 'balance' ? 'Balance' : 'Deposit' }} · Quote Q-{{ d.quoteNumber }}</span>
          <h1>{{ d.kind === 'balance' ? 'Pay your balance' : 'Hold your price' }}</h1>
          <p class="amount" data-testid="pay-amount">{{ d.amount | currency: d.currency }}</p>
          @if (d.kind === 'balance') {
            <p class="muted">What's left of {{ d.quoteTotal | currency: d.currency }} after your deposit.</p>
          } @else {
            <p class="muted">{{ d.percent }}% of {{ d.quoteTotal | currency: d.currency }}. It's taken off your final bill.</p>
          }
          @switch (d.status) {
            @case ('succeeded') {
              <p class="ok" data-testid="pay-done">{{ d.kind === 'balance' ? 'Paid in full. Thank you!' : 'Paid. Your price is held.' }}</p>
            }
            @case ('failed') {
              <div class="warning" role="alert" data-testid="pay-failed"><span>This payment didn't go through. Nothing was charged; start the deposit again from your project.</span></div>
            }
            @case ('unknown') {
              <p class="checking" data-testid="pay-checking">We're confirming this payment with the payment provider. This page updates by itself.</p>
            }
            @default {
              @if (d.provider === 'stripe') {
                @if (returned()) {
                  <p class="checking" data-testid="pay-checking">Confirming your payment…</p>
                } @else {
                  <button type="button" class="btn btn-primary wide" (click)="toProvider(d)" data-testid="pay-stripe">Continue to secure payment</button>
                  <p class="muted small">You pay on Stripe's page; we never see your card.</p>
                }
              } @else {
                <p class="test" data-testid="test-mode">Test mode: no card is charged.</p>
                <button type="button" class="btn btn-primary wide" (click)="pay()" [disabled]="paid.isLoading()" data-testid="pay-now">
                  {{ paid.isLoading() ? 'Paying…' : 'Pay ' + (d.amount | currency: d.currency) }}
                </button>
                <button type="button" class="btn link" (click)="pay('decline')" [disabled]="paid.isLoading()" data-testid="pay-decline">Decline (test a failed payment)</button>
              }
            }
          }
          @if (paid.error()) { <div class="warning" role="alert"><span>{{ describe(paid.error()) }}</span></div> }
          <div class="page-actions"><button type="button" class="btn" (click)="back()">Back to my project</button></div>
        } @else {
          <p class="muted">Loading…</p>
        }
      </section>
    </div>
  `,
  styles: `
    .pay { max-width: 28rem; margin: 2rem auto 0; display: grid; gap: .5rem; text-align: center; padding: 2rem 1.75rem; }
    .amount { font-family: var(--bp-font-heading); font-size: 2.6rem; font-weight: 700; margin: .25rem 0 0; }
    .test, .checking { margin: .5rem 0 0; padding: .4rem .8rem; border-radius: 999px; background: var(--bp-accent-soft); color: var(--bp-accent); font-size: .85rem; justify-self: center; }
    .ok { font-weight: 700; color: var(--bp-accent); }
    .wide { width: 100%; justify-content: center; }
    .link { justify-self: center; background: none; border: none; text-decoration: underline; font-size: .85rem; }
    .small { font-size: .85rem; }
  `,
})
export class PayPage {
  /** Bound from the route (/pay/:id). */
  readonly id = input.required<string>();
  /** ?return=stripe: back from Stripe Checkout. */
  readonly return = input<string | undefined>();
  private readonly router = inject(Router);

  protected readonly returned = computed(() => this.return() === 'stripe');
  /** Each tick asks again; while back from Stripe, the server checks with Stripe too (reconciliation). */
  private readonly tick = signal(0);
  protected readonly deposit = httpResource<DepositInfo>(() => {
    this.tick();
    return `/api/deposits/${encodeURIComponent(this.id())}${this.returned() ? '?refresh=true' : ''}`;
  });
  private readonly payRequest = signal<{ outcome: string | null; n: number } | null>(null);
  protected readonly paid = httpResource<DepositInfo>(() => {
    const r = this.payRequest();
    return r ? { url: `/api/deposits/${encodeURIComponent(this.id())}/pay`, method: 'POST', body: r.outcome ? { outcome: r.outcome } : {} } : undefined;
  });
  /** The newest answer: the pay result if there is one, else the last read. */
  protected readonly current = computed<DepositInfo | null>(() =>
    this.paid.hasValue() ? this.paid.value()! : this.deposit.hasValue() ? this.deposit.value()! : null,
  );
  private n = 0;

  constructor() {
    effect(() => {
      const d = this.current();
      if (d?.status === 'succeeded' && (this.paid.hasValue() || this.returned())) void this.router.navigate(['/projects', d.projectId]);
    });
    // Waiting for the provider's answer: ask again every 2 s (up to a minute).
    const timer = setInterval(() => {
      const d = this.deposit.hasValue() ? this.deposit.value() : null;
      const waiting = d && (d.status === 'unknown' || (d.status === 'processing' && this.returned()));
      if (waiting && this.tick() < 30 && !this.deposit.isLoading()) this.tick.update((t) => t + 1);
    }, 2000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  protected pay(outcome: string | null = null): void {
    this.payRequest.set({ outcome, n: ++this.n });
  }

  protected toProvider(d: DepositInfo): void {
    leaveFor.go(d.checkoutUrl);
  }

  protected back(): void {
    const d = this.current();
    void this.router.navigate(d ? ['/projects', d.projectId] : ['/projects']);
  }

  protected describe(err: unknown): string {
    return describeError(err as Parameters<typeof describeError>[0]);
  }
}
