import { Component, computed, inject } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { describeError } from '../../core/api/describe-error';
import { Category, Product, categoriesResource, productsResource } from '../catalog/catalog.api';
import { CompareSelection, compareRows, estimateItems } from './compare';

/** POST /api/quotes/estimates answer. Mirrors EstimateDto in C#. */
export interface Estimate {
  sku: string;
  quantity: number;
  total: number | null;
  currency: string | null;
  errors: { code: string; message: string }[];
}

/**
 * Side-by-side comparison (2k). Prices and the typical-project totals come from the server:
 * list prices from the catalog, totals from POST /api/quotes/estimates (QuotePricer). The UI only
 * lays them out; it never multiplies or adds money.
 */
@Component({
  selector: 'bp-compare-page',
  imports: [CurrencyPipe, RouterLink],
  template: `
    <div class="page">
      <header class="page-head">
        <span class="eyebrow">Compare</span>
        <h1>{{ category()?.name ?? 'Compare products' }}</h1>
        @if (category()?.typical; as t) {
          <p>Estimated totals are for {{ t.description }}, including tax, priced by the platform.</p>
        }
      </header>

      @if (selection.count() < 2) {
        <div class="card empty" data-testid="compare-empty">
          <p>Pick at least two products of the same kind in the catalog, then compare them here.</p>
          <a routerLink="/catalog" class="btn btn-primary">Go to the catalog</a>
        </div>
      } @else if (products.error()) {
        <div class="warning" role="alert" data-testid="compare-error"><span>{{ describe(products.error()) }}</span></div>
      } @else {
        <div class="scroller">
          <table class="compare" data-testid="compare-table">
            <thead>
              <tr>
                <th scope="col"><span class="sr-only">Attribute</span></th>
                @for (p of chosen(); track p.sku) {
                  <th scope="col" [class.best]="p.sku === cheapest()">
                    @if (p.imageUrl) {
                      <img [src]="p.imageUrl" [alt]="p.name" width="400" height="300" />
                    }
                    <span class="name">{{ p.name }}</span>
                    @if (p.sku === cheapest()) {
                      <span class="badge badge-accent" data-testid="best">Lowest estimate</span>
                    }
                    <button type="button" class="btn remove" (click)="selection.toggle(p)" [attr.aria-label]="'Remove ' + p.name">Remove</button>
                  </th>
                }
              </tr>
            </thead>
            <tbody>
              <tr>
                <th scope="row">Price</th>
                @for (p of chosen(); track p.sku) {
                  <td data-testid="unit-price">{{ p.listPrice | currency: p.currency }} <span class="muted">{{ p.unit === 'each' ? 'each' : 'per ' + p.unit }}</span></td>
                }
              </tr>
              @if (category()?.typical) {
                <tr class="estimate-row">
                  <th scope="row">Estimate</th>
                  @for (p of chosen(); track p.sku) {
                    <td data-testid="estimate">
                      @let e = estimateFor(p.sku);
                      @if (estimates.isLoading()) {
                        <span class="skeleton" style="width: 60%"></span>
                      } @else if (e?.total != null) {
                        <strong>{{ e!.total | currency: e!.currency ?? p.currency }}</strong>
                      } @else {
                        <span class="muted">Not priced this way (sold per {{ p.unit }})</span>
                      }
                    </td>
                  }
                </tr>
              }
              @for (row of rows(); track row.label) {
                <tr>
                  <th scope="row">{{ row.label }}</th>
                  @for (v of row.values; track $index) {
                    <td class="cap">{{ v }}</td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
        <p class="actions">
          <a routerLink="/catalog" class="btn">Back to the catalog</a>
          <button type="button" class="btn" (click)="selection.clear()">Clear comparison</button>
        </p>
      }
    </div>
  `,
  styles: `
    .empty { text-align: center; padding: 2.5rem; }
    .empty p { color: var(--bp-ink-muted); margin: 0 0 1rem; }
    .scroller { overflow-x: auto; border: 1px solid var(--bp-border); border-radius: var(--bp-radius-lg); background: var(--bp-surface); }
    .compare { width: 100%; border-collapse: collapse; min-width: 36rem; }
    .compare th, .compare td { padding: .75rem 1rem; text-align: left; vertical-align: top; border-bottom: 1px solid var(--bp-border); }
    .compare tbody th { color: var(--bp-ink-muted); font-weight: 600; font-size: .85rem; width: 9rem; }
    .compare thead th { font-weight: 600; }
    .compare thead th.best { background: var(--bp-accent-soft); }
    .compare img { display: block; width: 100%; max-width: 12rem; aspect-ratio: 4 / 3; height: auto; object-fit: cover; border-radius: var(--bp-radius-sm); margin-bottom: .5rem; }
    .name { display: block; font-family: var(--bp-font-heading); font-size: 1rem; margin-bottom: .4rem; }
    .remove { margin-top: .5rem; }
    .estimate-row td strong { font-family: var(--bp-font-heading); font-size: 1.25rem; color: var(--bp-accent); }
    .muted { color: var(--bp-ink-muted); font-size: .85rem; }
    .cap { text-transform: capitalize; }
    .actions { display: flex; gap: .75rem; margin-top: 1rem; flex-wrap: wrap; }
  `,
})
export class ComparePage {
  protected readonly selection = inject(CompareSelection);
  protected readonly products = productsResource();
  protected readonly categories = categoriesResource();
  protected readonly describe = describeError;

  protected readonly category = computed<Category | undefined>(() =>
    this.categories.value().find((c) => c.id === this.selection.category()),
  );
  /** In the order the customer picked them. */
  protected readonly chosen = computed<Product[]>(() => {
    const bySku = new Map(this.products.value().map((p) => [p.sku, p]));
    return this.selection.skus().map((s) => bySku.get(s)).filter((p): p is Product => !!p);
  });
  protected readonly rows = computed(() => compareRows(this.chosen(), this.category()));

  private readonly items = computed(() => estimateItems(this.chosen(), this.category()));
  protected readonly estimates = httpResource<Estimate[]>(
    () => (this.items().length ? { url: '/api/quotes/estimates', method: 'POST', body: { items: this.items() } } : undefined),
    { defaultValue: [] },
  );

  private readonly estimateBySku = computed(() => new Map(this.estimates.value().map((e) => [e.sku, e])));
  protected estimateFor(sku: string): Estimate | undefined {
    return this.estimateBySku().get(sku);
  }

  /** Lowest server-computed total (comparison only; no arithmetic on money here). */
  protected readonly cheapest = computed(() => {
    const priced = this.estimates.value().filter((e) => e.total != null);
    if (priced.length < 2) return null;
    return priced.reduce((a, b) => (b.total! < a.total! ? b : a)).sku;
  });
}
