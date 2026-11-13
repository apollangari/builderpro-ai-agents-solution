import { Component, computed, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { describeError } from '../../core/api/describe-error';
import { Product, productsResource } from './catalog.api';

/**
 * Your retailer's catalog. Server data is one httpResource; the search box is a plain signal,
 * and the visible list is computed from both. No RxJS, no subscriptions.
 */
@Component({
  selector: 'bp-catalog-page',
  imports: [CurrencyPipe],
  template: `
    <div class="page">
      <header class="page-head head-row">
        <div>
          <span class="eyebrow">Catalog</span>
          <h1>Your catalog</h1>
          <p>Products and list prices of the retailer you act for. Another retailer's products never reach this page.</p>
        </div>
        <div class="tools">
          <label class="search">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><circle cx="11" cy="11" r="7" /><path d="m20 20-3.5-3.5" /></svg>
            <input
              type="search"
              placeholder="Search name or SKU"
              aria-label="Search products"
              data-testid="search"
              [value]="query()"
              (input)="query.set($any($event.target).value)"
            />
          </label>
          <button type="button" class="btn" (click)="products.reload()" [disabled]="products.isLoading()">Refresh</button>
        </div>
      </header>

      @if (products.isLoading() && products.value().length === 0) {
        <div class="grid" data-testid="catalog-loading" aria-label="Loading">
          @for (i of placeholders; track i) {
            <div class="card product">
              <span class="skeleton" style="width: 40%"></span>
              <span class="skeleton" style="width: 85%; height: 1.2rem"></span>
              <span class="skeleton" style="width: 60%"></span>
            </div>
          }
        </div>
      } @else if (products.error()) {
        <div class="warning" role="alert" data-testid="catalog-error">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M12 3 2 20h20L12 3Z" /><path d="M12 10v4m0 3h.01" /></svg>
          <span>{{ describe(products.error()) }}</span>
        </div>
      } @else {
        <p class="count" data-testid="count">
          {{ visible().length }} of {{ products.value().length }} products
        </p>
        <div class="grid">
          @for (p of visible(); track p.sku) {
            <article class="card product" data-testid="product">
              @if (p.imageUrl) {
                <img class="thumb" [src]="p.imageUrl" [alt]="p.name" width="400" height="300" loading="lazy" data-testid="product-image" />
              }
              <div class="top">
                <span class="badge mono">{{ p.sku }}</span>
                <span class="unit">{{ p.unit === 'each' ? 'each' : 'per ' + p.unit }}</span>
              </div>
              <h2 class="card-title">{{ p.name }}</h2>
              @if (p.description) {
                <p class="desc">{{ p.description }}</p>
              }
              <div class="price" data-testid="price">{{ p.listPrice | currency: p.currency }}</div>
            </article>
          } @empty {
            <div class="empty card" data-testid="empty">
              @if (query()) {
                Nothing matches “{{ query() }}”.
              } @else {
                Your retailer has no active products yet.
              }
            </div>
          }
        </div>
      }
    </div>
  `,
  styles: `
    .head-row { display: flex; align-items: flex-end; justify-content: space-between; gap: 1rem; flex-wrap: wrap; }
    .tools { display: flex; gap: .6rem; align-items: center; flex-wrap: wrap; }
    .search {
      display: flex; align-items: center; gap: .5rem; min-height: var(--bp-tap); padding: 0 .9rem;
      border-radius: var(--bp-radius-md); background: var(--bp-surface); border: 1px solid var(--bp-border-strong);
      color: var(--bp-ink-muted); min-width: min(18rem, 100%);
    }
    .search:focus-within { outline: 2px solid var(--bp-accent); outline-offset: 2px; }
    .search svg { width: 1rem; height: 1rem; flex: none; }
    .search input { border: 0; outline: 0; background: transparent; color: var(--bp-ink); font: inherit; width: 100%; min-height: calc(var(--bp-tap) - 2px); }

    .count { color: var(--bp-ink-muted); font-size: .85rem; margin: 0 0 .9rem; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(16rem, 100%), 1fr)); gap: 1.1rem; }
    .product { display: flex; flex-direction: column; gap: .55rem; padding-top: 0; overflow: hidden; }
    .thumb { display: block; width: calc(100% + 40px); margin: 0 -20px .4rem; height: auto; aspect-ratio: 4 / 3; object-fit: cover; background: var(--bp-surface-muted); border-bottom: 1px solid var(--bp-border); }
    .product:not(:has(.thumb)) { padding-top: 20px; }
    .product:hover { border-color: var(--bp-border-strong); }
    .top { display: flex; justify-content: space-between; align-items: center; gap: .5rem; }
    .unit { color: var(--bp-ink-muted); font-size: .8rem; }
    .desc { margin: 0; color: var(--bp-ink-muted); font-size: .88rem; }
    .price { margin-top: auto; padding-top: .6rem; font-family: var(--bp-font-heading); font-size: 1.4rem; font-weight: 700; color: var(--bp-accent); }
    .empty { grid-column: 1 / -1; text-align: center; color: var(--bp-ink-muted); padding: 2.5rem; }
  `,
})
export class CatalogPage {
  protected readonly products = productsResource();
  protected readonly query = signal('');
  protected readonly describe = describeError;
  protected readonly placeholders = [1, 2, 3, 4, 5, 6];

  protected readonly visible = computed<Product[]>(() => {
    const q = this.query().trim().toLowerCase();
    const all = this.products.value();
    return q ? all.filter((p) => p.name.toLowerCase().includes(q) || p.sku.toLowerCase().includes(q)) : all;
  });
}
