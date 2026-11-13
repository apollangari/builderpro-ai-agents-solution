import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Router } from '@angular/router';
import { describeError } from '../../core/api/describe-error';
import { DepositInfo } from '../quote/guided';

/**
 * The deposit payment page (6a). In development the payment provider is "simulated": this page is
 * the test-mode checkout and "Pay" calls POST /api/deposits/{id}/pay. With a real provider (Stripe
 * Checkout, later) the customer is sent to the provider instead and the payment is confirmed by its
 * webhook. Either way the amount comes from the server, and only the customer can pay.
 */
@Component({
  selector: 'bp-pay-page',
  imports: [CurrencyPipe],
  template: `
    <div class="page">
      <section class="card pay" data-testid="pay-card">
        @if (deposit.error()) {
          <div class="warning" role="alert"><span>{{ describe(deposit.error()) }}</span></div>
        } @else if (deposit.hasValue()) {
          @let d = deposit.value()!;
          <span class="eyebrow">Deposit · Quote Q-{{ d.quoteNumber }}</span>
          <h1>Hold your price</h1>
          <p class="amount" data-testid="pay-amount">{{ d.amount | currency: d.currency }}</p>
          <p class="muted">{{ d.percent }}% of {{ d.quoteTotal | currency: d.currency }}. It's taken off your final bill.</p>
          @if (d.status === 'paid') {
            <p class="ok" data-testid="pay-done">Paid. Your price is held.</p>
          } @else {
            <p class="test" data-testid="test-mode">Test mode: no card is charged.</p>
            <button type="button" class="btn btn-primary wide" (click)="pay()" [disabled]="paid.isLoading()" data-testid="pay-now">
              {{ paid.isLoading() ? 'Paying…' : 'Pay ' + (d.amount | currency: d.currency) }}
            </button>
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
    .test { margin: .5rem 0 0; padding: .4rem .8rem; border-radius: 999px; background: var(--bp-accent-soft); color: var(--bp-accent); font-size: .85rem; justify-self: center; }
    .ok { font-weight: 700; color: var(--bp-accent); }
    .wide { width: 100%; justify-content: center; }
  `,
})
export class PayPage {
  /** Bound from the route (/pay/:id). */
  readonly id = input.required<string>();
  private readonly router = inject(Router);

  protected readonly deposit = httpResource<DepositInfo>(() => `/api/deposits/${encodeURIComponent(this.id())}`);
  private readonly payRequest = signal(0);
  protected readonly paid = httpResource<DepositInfo>(() =>
    this.payRequest() ? { url: `/api/deposits/${encodeURIComponent(this.id())}/pay`, method: 'POST', body: {} } : undefined,
  );
  private readonly projectId = computed(() => (this.deposit.hasValue() ? this.deposit.value()!.projectId : null));

  constructor() {
    effect(() => {
      if (this.paid.hasValue() && this.paid.value()!.status === 'paid') void this.router.navigate(['/projects', this.paid.value()!.projectId]);
    });
  }

  protected pay(): void {
    this.payRequest.update((n) => n + 1);
  }

  protected back(): void {
    const id = this.projectId();
    void this.router.navigate(id ? ['/projects', id] : ['/projects']);
  }

  protected describe(err: unknown): string {
    return describeError(err as Parameters<typeof describeError>[0]);
  }
}
