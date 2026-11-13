import { Component, computed, inject, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { CompareSelection, MAX_COMPARE } from '../compare/compare';
import { describeError } from '../../core/api/describe-error';
import { Category, categoriesResource, productsResource } from './catalog.api';
import { Filters, applyPicker, categoryTabs, filterGroups, keyAttributes } from './picker';

/**
 * Product picker (2j): category tabs, attribute filters, image cards.
 * Signal-first: two httpResources (products, categories), three signals (category, filters, query),
 * and everything on screen is a computed of those. No RxJS, no subscriptions.
 */
@Component({
  selector: 'bp-catalog-page',
  imports: [CurrencyPipe, RouterLink],
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
        <nav class="tabs" role="tablist" aria-label="Categories" data-testid="category-tabs">
          @for (t of tabs(); track t.id) {
            <button
              type="button"
              role="tab"
              class="tab"
              [class.active]="t.id === category()"
              [attr.aria-selected]="t.id === category()"
              [attr.data-testid]="'tab-' + (t.id ?? 'all')"
              (click)="selectCategory(t.id)"
            >
              {{ t.name }} <span class="tab-count">{{ t.count }}</span>
            </button>
          }
        </nav>

        @if (groups().length > 0) {
          <div class="filters" data-testid="filters">
            @for (g of groups(); track g.key) {
              <label class="filter">
                <span>{{ g.label }}</span>
                <select [attr.data-testid]="'filter-' + g.key" [value]="filters()[g.key] ?? ''" (change)="setFilter(g.key, $any($event.target).value)">
                  <option value="">Any</option>
                  @for (v of g.values; track v.value) {
                    <option [value]="v.value">{{ v.value.replaceAll('-', ' ') }} ({{ v.count }})</option>
                  }
                </select>
              </label>
            }
            @if (hasFilters()) {
              <button type="button" class="btn" data-testid="clear-filters" (click)="filters.set({})">Clear filters</button>
            }
          </div>
        }

        <p class="count" data-testid="count">{{ visible().length }} of {{ products.value().length }} products</p>

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
              @if (badges(p.sku).length) {
                <div class="chips" data-testid="attributes">
                  @for (a of badges(p.sku); track a) {
                    <span class="badge">{{ a }}</span>
                  }
                </div>
              }
              @if (p.description) {
                <p class="desc">{{ p.description }}</p>
              }
              <div class="price" data-testid="price">{{ p.listPrice | currency: p.currency }}</div>
              @if (p.category !== 'labour' && p.category !== 'other') {
                <a class="see-it" [routerLink]="['/visualize']" [queryParams]="{ sku: p.sku }" [attr.data-testid]="'visualize-' + p.sku">See it in a room</a>
              }
              @if (p.category !== 'labour') {
                <label class="compare-toggle">
                  <input
                    type="checkbox"
                    [attr.data-testid]="'compare-' + p.sku"
                    [checked]="compare.has(p.sku)"
                    [disabled]="!compare.has(p.sku) && compare.full() && compare.category() === p.category"
                    (change)="compare.toggle(p)"
                  />
                  Compare
                </label>
              }
            </article>
          } @empty {
            <div class="empty card" data-testid="empty">
              @if (query() || hasFilters()) {
                Nothing matches{{ query() ? ' "' + query() + '"' : '' }}{{ hasFilters() ? ' with these filters' : '' }}.
              } @else {
                Your retailer has no active products here yet.
              }
            </div>
          }
        </div>

        @if (compare.count() > 0) {
          <div class="compare-bar" role="region" aria-label="Comparison" data-testid="compare-bar">
            <span>{{ compare.count() }} of {{ maxCompare }} selected</span>
            <button type="button" class="btn" (click)="compare.clear()">Clear</button>
            @if (compare.count() >= 2) {
              <a routerLink="/compare" class="btn btn-primary" data-testid="go-compare">Compare {{ compare.count() }}</a>
            } @else {
              <span class="hint">Pick one more of the same kind</span>
            }
          </div>
        }
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

    .tabs { display: flex; gap: .4rem; overflow-x: auto; padding-bottom: .25rem; margin-bottom: 1rem; scrollbar-width: thin; }
    .tab {
      flex: none; display: inline-flex; align-items: center; gap: .45rem; min-height: var(--bp-tap); padding: 0 1rem;
      border-radius: 999px; border: 1px solid var(--bp-border-strong); background: var(--bp-surface);
      color: var(--bp-ink); font: inherit; font-weight: 600; font-size: .9rem; cursor: pointer;
    }
    .tab:hover { background: var(--bp-surface-muted); }
    .tab.active { background: var(--bp-accent); border-color: var(--bp-accent); color: var(--bp-on-accent); }
    .tab-count { font-size: .75rem; font-weight: 600; opacity: .8; }

    .filters { display: flex; flex-wrap: wrap; align-items: flex-end; gap: .75rem; margin-bottom: 1rem; }
    .filter { display: grid; gap: .25rem; font-size: .8rem; color: var(--bp-ink-muted); font-weight: 600; }
    .filter select {
      min-height: var(--bp-tap); min-width: 10rem; padding: 0 .75rem; border-radius: var(--bp-radius-md);
      border: 1px solid var(--bp-border-strong); background: var(--bp-surface); color: var(--bp-ink); font: inherit; text-transform: capitalize;
    }

    .count { color: var(--bp-ink-muted); font-size: .85rem; margin: 0 0 .9rem; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(16rem, 100%), 1fr)); gap: 1.1rem; }
    .product { display: flex; flex-direction: column; gap: .55rem; padding-top: 0; overflow: hidden; }
    .thumb { display: block; width: calc(100% + 40px); margin: 0 -20px .4rem; height: auto; aspect-ratio: 4 / 3; object-fit: cover; background: var(--bp-surface-muted); border-bottom: 1px solid var(--bp-border); }
    .product:not(:has(.thumb)) { padding-top: 20px; }
    .product:hover { border-color: var(--bp-border-strong); }
    .top { display: flex; justify-content: space-between; align-items: center; gap: .5rem; }
    .unit { color: var(--bp-ink-muted); font-size: .8rem; }
    .chips .badge { text-transform: capitalize; }
    .desc { margin: 0; color: var(--bp-ink-muted); font-size: .88rem; }
    .price { margin-top: auto; padding-top: .6rem; font-family: var(--bp-font-heading); font-size: 1.4rem; font-weight: 700; color: var(--bp-accent); }
    .empty { grid-column: 1 / -1; text-align: center; color: var(--bp-ink-muted); padding: 2.5rem; }
    .see-it { display: inline-flex; align-items: center; min-height: var(--bp-tap); font-weight: 600; font-size: .9rem; }
    .compare-toggle { display: inline-flex; align-items: center; gap: .5rem; min-height: var(--bp-tap); font-weight: 600; font-size: .9rem; cursor: pointer; }
    .compare-toggle input { width: 1.2rem; height: 1.2rem; accent-color: var(--bp-accent); }
    .compare-bar {
      position: sticky; bottom: 1rem; z-index: 5; margin-top: 1.25rem; display: flex; align-items: center; gap: .75rem; flex-wrap: wrap;
      padding: .6rem .9rem; background: var(--bp-surface); border: 1px solid var(--bp-border-strong); border-radius: var(--bp-radius-lg);
    }
    .compare-bar span:first-child { font-weight: 600; margin-right: auto; }
    .compare-bar .hint { color: var(--bp-ink-muted); font-size: .85rem; }
  `,
})
export class CatalogPage {
  protected readonly products = productsResource();
  protected readonly categories = categoriesResource();
  protected readonly describe = describeError;
  protected readonly placeholders = [1, 2, 3, 4, 5, 6];

  protected readonly compare = inject(CompareSelection);
  protected readonly maxCompare = MAX_COMPARE;

  protected readonly category = signal<string | null>(null);
  protected readonly filters = signal<Filters>({});
  protected readonly query = signal('');

  private readonly categoryById = computed(() => new Map<string, Category>(this.categories.value().map((c) => [c.id, c])));

  protected readonly tabs = computed(() => categoryTabs(this.products.value(), this.categories.value()));
  protected readonly groups = computed(() => {
    const id = this.category();
    return filterGroups(this.products.value(), id ? this.categoryById().get(id) : undefined);
  });
  protected readonly hasFilters = computed(() => Object.values(this.filters()).some((v) => !!v));
  protected readonly visible = computed(() => applyPicker(this.products.value(), this.category(), this.filters(), this.query()));

  /** Card badges per SKU, recomputed only when the data changes. */
  private readonly badgeMap = computed(
    () => new Map(this.products.value().map((p) => [p.sku, keyAttributes(p, this.categoryById().get(p.category))])),
  );
  protected badges(sku: string): string[] {
    return this.badgeMap().get(sku) ?? [];
  }

  protected selectCategory(id: string | null): void {
    this.category.set(id);
    this.filters.set({}); // filters belong to a category
  }

  protected setFilter(key: string, value: string): void {
    this.filters.update((f) => ({ ...f, [key]: value }));
  }
}
