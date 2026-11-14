import { Component, inject, signal } from '@angular/core';
import { CurrencyPipe, DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Router, RouterLink } from '@angular/router';
import { describeError } from '../../core/api/describe-error';
import { StoreFeatures } from '../../core/tenant/store-features';

/** A customer of this store, as Identity returns it to a salesperson (1i). */
export interface CustomerHit {
  customerId: string;
  name: string;
  email: string;
  phone: string | null;
  postalCode: string | null;
}

export interface SaleLine {
  projectId: string;
  projectTitle: string;
  customerId: string | null;
  quoteNumber: number;
  quoteTotal: number;
  deposit: number;
  currency: string;
  paidAt: string;
}

export interface SalespersonCommission {
  salespersonKey: string;
  name: string | null;
  sales: number;
  soldTotal: number;
  currency: string;
  commissionPercent: number | null;
  commission: number | null;
  lines: SaleLine[];
}

export interface CommissionReport {
  from: string;
  to: string;
  salespeople: SalespersonCommission[];
}

/** The store's default ZIP when a customer has none on file. */
const FALLBACK_ZIP = '75078';

/**
 * Salesperson desk (1i). Find a customer of this store, start a project for them, then build the
 * quote in the same workspace the customer uses. The customer accepts and pays the deposit in their
 * own account; the sale is credited here once the deposit is paid. Managers ("sales.report") see
 * every salesperson's line.
 */
@Component({
  selector: 'bp-sales-page',
  imports: [CurrencyPipe, DatePipe, RouterLink],
  template: `
    <div class="page">
      <header class="page-head">
        <span class="eyebrow">Sales</span>
        <h1>Quote for a customer</h1>
      </header>

      @if (features.salesperson()) {
        <section class="card" data-testid="customer-search">
          <h2 class="card-title">Find the customer</h2>
          <form class="search" (submit)="$event.preventDefault(); find()">
            <input type="search" [value]="text()" (input)="text.set($any($event.target).value)" placeholder="Name, email or phone"
                   aria-label="Find a customer" data-testid="customer-query" />
            <button type="submit" class="btn btn-primary" [disabled]="text().trim().length < 2" data-testid="customer-find">Find</button>
          </form>
          @if (customers.error()) {
            <div class="warning" role="alert"><span>{{ describe(customers.error()) }}</span></div>
          } @else if (customers.hasValue()) {
            @if (customers.value().length === 0) {
              <p class="muted" data-testid="no-customers">No customer of this store matches. Ask them to sign up with the store's link first.</p>
            }
            <ul class="hits">
              @for (c of customers.value(); track c.customerId) {
                <li class="hit" data-testid="customer-hit">
                  <div>
                    <strong>{{ c.name || c.email }}</strong>
                    <span class="muted">{{ c.email }}@if (c.phone) { · {{ c.phone }} }@if (c.postalCode) { · ZIP {{ c.postalCode }} }</span>
                  </div>
                  <button type="button" class="btn btn-primary" (click)="start(c)" data-testid="start-for">Start project</button>
                </li>
              }
            </ul>
          }
          <p class="muted small">You prepare the project and the quote; the customer accepts it and pays the deposit in their own account.</p>
        </section>
      }

      <section class="card" data-testid="commission">
        <h2 class="card-title">Sales this month</h2>
        @if (report.error()) {
          <div class="warning" role="alert"><span>{{ describe(report.error()) }}</span></div>
        } @else if (report.hasValue()) {
          @let r = report.value();
          <p class="muted small">{{ r.from | date: 'MMMM y' : 'UTC' }}. A sale counts once the customer's deposit is paid.</p>
          <table class="table">
            <thead><tr><th>Salesperson</th><th>Sales</th><th>Sold</th><th>Commission</th></tr></thead>
            <tbody>
              @for (s of r.salespeople; track s.salespersonKey) {
                <tr data-testid="commission-row">
                  <td>{{ s.name ?? 'Salesperson' }}</td>
                  <td>{{ s.sales }}</td>
                  <td>{{ s.soldTotal | currency: s.currency }}</td>
                  <td>
                    @if (s.commission !== null) { {{ s.commission | currency: s.currency }} ({{ s.commissionPercent }}%) }
                    @else { <span class="muted">rate not set</span> }
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="4" class="muted">No sales yet.</td></tr>
              }
            </tbody>
          </table>
        } @else {
          <p class="muted">Loading…</p>
        }
      </section>

      <div class="page-actions"><a routerLink="/projects" class="btn">Store projects</a></div>
    </div>
  `,
  styles: `
    .search { display: flex; gap: .5rem; }
    .search input { flex: 1; min-width: 0; box-sizing: border-box; }
    .hits { list-style: none; margin: .75rem 0 0; padding: 0; display: grid; gap: .5rem; }
    .hit { display: flex; justify-content: space-between; align-items: center; gap: 1rem; padding: .6rem .8rem; border: 1px solid var(--bp-border); border-radius: .6rem; }
    .hit div { display: grid; gap: .15rem; min-width: 0; }
    .table { width: 100%; border-collapse: collapse; }
    .table th, .table td { text-align: left; padding: .45rem .5rem; border-bottom: 1px solid var(--bp-border); }
    .small { font-size: .85rem; }
  `,
})
export class SalesPage {
  protected readonly features = inject(StoreFeatures);
  private readonly router = inject(Router);

  protected readonly text = signal('');
  private readonly query = signal<string | null>(null);
  protected readonly customers = httpResource<CustomerHit[]>(() => {
    const q = this.query();
    return q ? `/api/identity/customers?q=${encodeURIComponent(q)}` : undefined;
  });
  protected readonly report = httpResource<CommissionReport>(() => '/api/sales/commission');

  protected find(): void {
    const q = this.text().trim();
    if (q.length >= 2) this.query.set(q);
  }

  /** The same ZIP, type and product steps the customer would take, prepared for them (1i). */
  protected start(customer: CustomerHit): void {
    void this.router.navigate(['/quote/new'], {
      queryParams: { for: customer.customerId, name: customer.name || customer.email, zip: customer.postalCode || FALLBACK_ZIP },
    });
  }

  protected describe(err: unknown): string {
    return describeError(err as Parameters<typeof describeError>[0]);
  }
}
