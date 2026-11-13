import { Component, computed, effect, inject, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Router } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { describeError } from '../../core/api/describe-error';
import { Product, categoriesResource } from '../catalog/catalog.api';
import { applyPicker, categoryTabs, filterGroups, keyAttributes } from '../catalog/picker';
import { CatalogChatComponent, CatalogFilter } from './catalog-chat.component';
import { StoreFeatures } from '../../core/tenant/store-features';
import { Project } from './guided';
import { ProgressTrackerComponent } from '../../shared/progress-tracker.component';

/** GET /api/catalog/service-area answer. Mirrors ServiceAreaDto in C#: yes/no only, never the store's ZIP lists. */
interface ServiceArea {
  zip: string; // normalized by the server ("75201", "K1A 0B1")
  served: boolean;
}

type Step = 'zip' | 'type' | 'product';

/**
 * Start a project, ONE step per screen (owner, 2n-f): 1. ZIP (the account's, or another), 2. project
 * type (only types the store sells in that ZIP), 3. product at that ZIP's price. Choosing a product creates the project and opens
 * its workspace (measure, options, protection, quote).
 *
 *  - Types come from what the store sells; products and prices come from /api/catalog/products?zip=.
 *  - The page never computes or adjusts a price. Signal-first: every request is an httpResource.
 */
@Component({
  selector: 'bp-new-quote-page',
  imports: [CurrencyPipe, ProgressTrackerComponent, CatalogChatComponent],
  template: `
    <div class="page">
      <bp-progress-tracker [steps]="stepLabels" [current]="step()" />

      @switch (step()) {
        @case ('zip') {
          <section class="zip-card card" data-testid="step-zip">
            <h1 class="title">Where's your project?</h1>
            <p class="muted">We'll show what your store offers there, at that area's prices.</p>

            @if (profileZip() && !editing()) {
              <div class="zip-big" aria-hidden="true">{{ profileZip() }}</div>
              <p class="muted small">The ZIP from your account</p>
              <button type="button" class="btn btn-primary wide" (click)="useZip(profileZip()!)" [disabled]="area.isLoading()" data-testid="zip-yes">
                {{ area.isLoading() ? 'Checking…' : 'Use ZIP ' + profileZip() }}
              </button>
              <button type="button" class="link" (click)="editing.set(true)" data-testid="zip-no">Use a different ZIP</button>
            } @else {
              <form class="zip-form" (submit)="$event.preventDefault(); useZip(zipInput())">
                <label class="sr-only" for="zip-input">ZIP code of the project</label>
                <input id="zip-input" type="text" inputmode="text" autocomplete="postal-code" maxlength="10" placeholder="ZIP code"
                       [value]="zipInput()" (input)="zipInput.set($any($event.target).value)" data-testid="zip-input" />
                <button type="submit" class="btn btn-primary wide" [disabled]="!zipInput().trim() || area.isLoading()" data-testid="zip-continue">
                  {{ area.isLoading() ? 'Checking…' : 'Continue' }}
                </button>
              </form>
              @if (profileZip()) { <button type="button" class="link" (click)="editing.set(false)">Use ZIP {{ profileZip() }} instead</button> }
            }

            @if (area.hasValue() && !area.value()!.served) {
              <div class="warning" role="alert" data-testid="zip-not-served">
                <span>We don't serve ZIP {{ area.value()!.zip }} yet. Try the ZIP where the work will happen.</span>
              </div>
            } @else if (area.error()) {
              <div class="warning" role="alert" data-testid="zip-error"><span>{{ describe(area.error()) }}</span></div>
            }
          </section>
        }

        @case ('type') {
          <section class="step" data-testid="step-category">
            <div class="title-row">
              <h1 class="title">What are you working on?</h1>
              <button type="button" class="zip-pill" (click)="changeZip()" data-testid="zip-change" [attr.aria-label]="'Project ZIP ' + servedZip() + ', change'">
                <span aria-hidden="true">📍</span> ZIP {{ servedZip() }} <span class="change">Change</span>
              </button>
            </div>
            @if (products.isLoading()) {
              <p class="muted">Loading…</p>
            } @else if (products.error()) {
              <div class="warning" role="alert"><span>{{ describe(products.error()) }}</span></div>
            } @else if (types().length === 0) {
              <p class="muted" data-testid="no-products">Your store has nothing for this ZIP yet.</p>
            } @else {
              <div class="tiles">
                @for (t of types(); track t.id) {
                  <button type="button" class="tile" (click)="pickCategory(t.id!)" [attr.data-testid]="'type-' + t.id">
                    @if (pictureOf(t.id!); as pic) { <img [src]="pic" alt="" width="320" height="200" loading="lazy" /> }
                    <span class="tile-name">{{ t.name }}</span>
                  </button>
                }
              </div>
            }
          </section>
        }

        @case ('product') {
          <section class="step" data-testid="step-product">
            <div class="title-row">
              <h1 class="title">Choose your {{ typeName().toLowerCase() }}</h1>
              <button type="button" class="zip-pill" (click)="changeZip()" [attr.aria-label]="'Project ZIP ' + servedZip() + ', change'">
                <span aria-hidden="true">📍</span> ZIP {{ servedZip() }} <span class="change">Change</span>
              </button>
            </div>
            <div class="browse" [class.no-chat]="!features.anyChat()">
              @if (features.anyChat()) {
                <bp-catalog-chat [zip]="servedZip()!" [category]="category()!" [categoryName]="typeName()" (filtered)="chatFilter.set($event)" />
              }
              <div class="catalog">
                @if (filterGroupsHere().length || chatFilter()) {
                  <div class="filters" data-testid="filters">
                    @if (chatFilter(); as cf) {
                      <span class="chip active" data-testid="chat-filter">{{ describeFilter(cf) }}
                        <button type="button" class="x" (click)="chatFilter.set(null)" aria-label="Clear the concierge's filter" data-testid="clear-chat-filter">×</button>
                      </span>
                    }
                    @for (g of filterGroupsHere(); track g.key) {
                      <label class="filter">{{ g.label }}
                        <select [value]="manual()[g.key] ?? ''" (change)="setManual(g.key, $any($event.target).value)" [attr.data-testid]="'filter-' + g.key">
                          <option value="">Any</option>
                          @for (v of g.values; track v.value) { <option [value]="v.value">{{ v.value.replace('-', ' ') }} ({{ v.count }})</option> }
                        </select>
                      </label>
                    }
                  </div>
                }
                @if (products.isLoading()) {
                  <p class="muted">Loading products for your area…</p>
                } @else if (inCategory().length === 0) {
                  <p class="muted" data-testid="no-products">Your store doesn't offer {{ typeName().toLowerCase() }} in this ZIP yet.</p>
                } @else if (shown().length === 0) {
                  <p class="muted" data-testid="no-matches">Nothing matches. <button type="button" class="link" (click)="clearFilters()">Show all</button></p>
                } @else {
                  <p class="muted small" data-testid="shown-count">{{ shown().length }} of {{ inCategory().length }} · prices per unit for ZIP {{ servedZip() }}</p>
                  <div class="grid">
                    @for (p of shown(); track p.sku) {
                      <article class="product" [attr.data-testid]="'product-' + p.sku">
                        @if (p.imageUrl) { <img [src]="p.imageUrl" alt="" width="320" height="200" loading="lazy" /> }
                        <h2>{{ p.name }}</h2>
                        <div class="chips">
                          @for (a of badges(p); track a) { <span class="badge">{{ a }}</span> }
                        </div>
                        <p class="price" data-testid="price">{{ p.listPrice | currency: p.currency }} <span class="muted">/ {{ unitLabel(p.unit) }}</span></p>
                        <button type="button" class="btn btn-primary" (click)="choose(p)" [disabled]="starting()" [attr.data-testid]="'choose-' + p.sku">
                          {{ starting() && selected() === p.sku ? 'Starting…' : 'Choose' }}
                        </button>
                      </article>
                    }
                  </div>
                }
                @if (startError()) { <div class="warning" role="alert"><span>{{ startError() }}</span></div> }
              </div>
            </div>
            <div class="nav"><button type="button" class="btn" (click)="backToType()" data-testid="back">Back</button></div>
          </section>
        }
      }
    </div>
  `,
  styles: `
    .steps { display: flex; flex-wrap: wrap; gap: .4rem; list-style: none; padding: 0; margin: 0 0 1rem; counter-reset: step; }
    .steps li { counter-increment: step; padding: .3rem .75rem; border-radius: 999px; border: 1px solid var(--bp-border); color: var(--bp-ink-muted); font-size: .85rem; }
    .steps li::before { content: counter(step) '. '; }
    .steps li.current { border-color: var(--bp-accent); background: var(--bp-accent); color: var(--bp-on-accent); font-weight: 600; }
    .steps li.done { background: var(--bp-accent-soft); color: var(--bp-accent); border-color: transparent; }
    :host { display: block; }
    .page { max-width: 72rem; margin: 0 auto; }
    .step { display: grid; gap: 1rem; }
    .title-row { display: flex; justify-content: space-between; align-items: center; gap: 1rem; flex-wrap: wrap; }
    .zip-pill {
      display: inline-flex; align-items: center; gap: .4rem; min-height: 2.5rem; padding: 0 1rem; border-radius: 999px;
      border: 1px solid var(--bp-border-strong); background: var(--bp-surface); color: var(--bp-ink); font: inherit; font-weight: 600; cursor: pointer;
    }
    .zip-pill .change { color: var(--bp-accent); font-weight: 400; text-decoration: underline; }
    .browse { display: grid; gap: 1rem; grid-template-columns: minmax(16rem, 22rem) minmax(0, 1fr); align-items: stretch; }
    @media (max-width: 900px) { .browse { grid-template-columns: 1fr; } }
    .browse.no-chat { grid-template-columns: 1fr; }
    .catalog { display: grid; gap: .75rem; }
    .filters { display: flex; flex-wrap: wrap; gap: .5rem; align-items: center; }
    .filter { display: flex; align-items: center; gap: .4rem; font-size: .9rem; color: var(--bp-ink-muted); }
    .filter select { min-height: 2.5rem; border-radius: var(--bp-radius-md); border: 1px solid var(--bp-border-strong); background: var(--bp-surface); color: var(--bp-ink); font: inherit; padding: 0 .5rem; }
    .chip { display: inline-flex; align-items: center; gap: .3rem; padding: .3rem .4rem .3rem .8rem; border-radius: 999px; background: var(--bp-accent-soft); color: var(--bp-accent); font-weight: 600; }
    .chip .x { border: 0; background: none; color: inherit; font-size: 1.1rem; cursor: pointer; min-width: 1.75rem; min-height: 1.75rem; }
    .zip-card { max-width: 26rem; margin: 2rem auto 0; display: grid; gap: .6rem; justify-items: center; text-align: center; padding: 2rem 1.75rem; }
    .zip-big { font-family: var(--bp-font-heading); font-size: 2.6rem; font-weight: 700; letter-spacing: .06em; color: var(--bp-ink); line-height: 1.1; margin-top: .5rem; }
    .small { font-size: .85rem; margin: 0; }
    .wide { width: 100%; justify-content: center; }
    .zip-form { display: grid; gap: .6rem; width: 100%; margin-top: .5rem; }
    .zip-form input { width: 100%; text-align: center; font-size: 1.4rem; letter-spacing: .06em; box-sizing: border-box; }
    .title { margin: 0; font-size: 1.5rem; }
    .lead { font-size: 1.15rem; margin: 0; }
    .row { display: flex; gap: .6rem; flex-wrap: wrap; }
    .field-label { display: block; font-weight: 600; margin-bottom: .35rem; }
    input[type='text'] {
      min-height: var(--bp-tap); padding: 0 .9rem; border-radius: var(--bp-radius-md); width: 12rem;
      border: 1px solid var(--bp-border-strong); background: var(--bp-surface); color: var(--bp-ink); font: inherit;
    }
    .actions { display: flex; gap: .6rem; flex-wrap: wrap; }
    .nav { display: flex; gap: .6rem; flex-wrap: wrap; justify-content: flex-end; }
    .link { min-height: var(--bp-tap); background: none; border: 0; color: var(--bp-accent); text-decoration: underline; cursor: pointer; font: inherit; }
    .tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(12rem, 1fr)); gap: 1rem; }
    .tile {
      display: grid; gap: .35rem; text-align: left; padding: .6rem; cursor: pointer; font: inherit; color: var(--bp-ink);
      border: 1px solid var(--bp-border); border-radius: var(--bp-radius-md); background: var(--bp-surface);
    }
    .tile:hover, .tile:focus-visible { border-color: var(--bp-accent); }
    .tile img { width: 100%; height: auto; aspect-ratio: 16 / 9; object-fit: cover; border-radius: var(--bp-radius-sm, 6px); }
    .tile-name { font-weight: 600; padding: 0 .2rem .2rem; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(13rem, 1fr)); gap: .75rem; }
    .product { display: grid; gap: .4rem; align-content: start; padding: .6rem; border: 1px solid var(--bp-border); border-radius: var(--bp-radius-md); }
    .product img { width: 100%; height: auto; aspect-ratio: 16 / 9; object-fit: cover; border-radius: var(--bp-radius-sm, 6px); }
    .product h2 { margin: 0; font-size: 1rem; }
    .price { margin: 0; font-weight: 700; }
  `,
})
export class NewQuotePage {
  private readonly oidc = inject(OidcSecurityService);
  private readonly router = inject(Router);
  /** The store's package (1h): the chat beside the catalog only when a chat is included. */
  protected readonly features = inject(StoreFeatures);

  protected readonly stepLabels = [
    { id: 'zip', label: 'ZIP' },
    { id: 'type', label: 'Type' },
    { id: 'product', label: 'Product' },
    { id: 'measure', label: 'Measure' },
    { id: 'options', label: 'Options' },
    { id: 'protection', label: 'Protection' },
    { id: 'quote', label: 'Quote' },
  ];

  /** The ZIP from the sign-up address (ID token claim postal_code), or null. */
  protected readonly profileZip = computed(() => {
    const claims = this.oidc.userData().userData as { postal_code?: string } | null;
    return claims?.postal_code?.trim() || null;
  });

  protected readonly category = signal<string | null>(null);
  protected readonly editing = signal(false);
  protected readonly zipInput = signal('');
  /** The ZIP being checked or in use; null until the customer answers. */
  protected readonly zip = signal<string | null>(null);
  protected readonly selected = signal<string | null>(null);

  protected readonly area = httpResource<ServiceArea>(() => {
    const zip = this.zip();
    return zip ? `/api/catalog/service-area?zip=${encodeURIComponent(zip)}` : undefined;
  });
  protected readonly served = computed(() => this.area.hasValue() && this.area.value()!.served);
  protected readonly servedZip = computed(() => (this.served() ? this.area.value()!.zip : null));

  protected readonly step = computed<Step>(() => (!this.served() ? 'zip' : !this.category() ? 'type' : 'product'));
  protected readonly stepIndex = computed(() => ['zip', 'type', 'product'].indexOf(this.step()));

  /** Only products sold in this ZIP, at this ZIP's price. */
  protected readonly products = httpResource<Product[]>(
    () => {
      const zip = this.servedZip();
      return zip ? `/api/catalog/products?zip=${encodeURIComponent(zip)}` : undefined;
    },
    { defaultValue: [] },
  );
  private readonly categories = categoriesResource();

  /** Project types: categories the store sells that are projects (labour and "other" have no scene). */
  protected readonly types = computed(() => {
    const projects = this.categories.value().filter((c) => c.scene !== 'none');
    return categoryTabs(this.products.value(), projects).filter((t) => t.id !== null);
  });
  protected readonly typeName = computed(() => this.types().find((t) => t.id === this.category())?.name ?? 'product');
  protected readonly inCategory = computed(() => this.products.value().filter((p) => p.category === this.category()));

  // ---- the product screen's filters: the concierge's (server-applied) and the customer's own ----
  protected readonly chatFilter = signal<CatalogFilter | null>(null);
  protected readonly manual = signal<Record<string, string>>({});
  protected readonly filterGroupsHere = computed(() =>
    filterGroups(this.inCategory(), this.categories.value().find((c) => c.id === this.category())),
  );
  protected readonly shown = computed(() => {
    const chat = this.chatFilter();
    const base = chat ? this.inCategory().filter((p) => chat.skus.includes(p.sku)) : this.inCategory();
    return applyPicker(base, null, this.manual(), '');
  });

  protected setManual(key: string, value: string): void {
    this.manual.update((m) => ({ ...m, [key]: value }));
  }

  protected clearFilters(): void {
    this.chatFilter.set(null);
    this.manual.set({});
  }

  /** "quartz · white · under $90" for the chip. */
  protected describeFilter(f: CatalogFilter): string {
    const parts = [...Object.values(f.attributes), f.words ?? ''].filter(Boolean);
    if (f.minPrice != null) parts.push(`from $${f.minPrice}`);
    if (f.maxPrice != null) parts.push(`under $${f.maxPrice}`);
    return parts.length ? parts.join(' · ') : 'Concierge picks';
  }

  // ---- choosing a product creates the project, saves the product, opens the workspace ----
  private readonly createReq = signal<{ zip: string; title: string } | null>(null);
  private readonly created = httpResource<Project>(() => {
    const body = this.createReq();
    return body ? { url: '/api/projects', method: 'POST', body } : undefined;
  });
  private readonly productReq = signal<{ id: string; sku: string } | null>(null);
  private readonly productSaved = httpResource<Project>(() => {
    const r = this.productReq();
    return r ? { url: `/api/projects/${encodeURIComponent(r.id)}/answers`, method: 'PUT', body: { sku: r.sku, measurements: null, choices: [] } } : undefined;
  });
  protected readonly starting = computed(() => this.created.isLoading() || this.productSaved.isLoading());
  protected readonly startError = computed(() => {
    const err = this.created.error() ?? this.productSaved.error();
    return err ? this.describe(err) : null;
  });

  constructor() {
    effect(() => {
      const sku = this.selected();
      if (this.created.hasValue() && sku && !this.productReq()) this.productReq.set({ id: this.created.value()!.id, sku });
    });
    effect(() => {
      if (this.productSaved.hasValue()) void this.router.navigate(['/projects', this.productSaved.value()!.id]);
    });
  }

  protected pickCategory(id: string): void {
    this.category.set(id);
    this.clearFilters();
  }

  protected backToType(): void {
    this.category.set(null);
  }

  protected useZip(zip: string): void {
    const value = zip.trim();
    if (value) this.zip.set(value);
  }

  protected changeZip(): void {
    this.zipInput.set('');
    this.zip.set(null);
    this.category.set(null);
    this.editing.set(true);
  }

  protected choose(p: Product): void {
    const zip = this.servedZip();
    if (!zip || this.starting()) return;
    this.selected.set(p.sku);
    this.createReq.set({ zip, title: `${this.typeName()} · ${p.name}` });
  }

  /** Platform pictures for types whose first product isn't a good picture (roofing's could be drip edge). */
  private static readonly TYPE_PICTURES: Record<string, string> = { roofing: '/products/roof-shingle-arch.svg' };

  /** A project type's picture: the platform's, else one of the store's products of that type. */
  protected pictureOf(category: string): string | null {
    return NewQuotePage.TYPE_PICTURES[category] ?? this.products.value().find((p) => p.category === category && p.imageUrl)?.imageUrl ?? null;
  }

  protected badges(p: Product): string[] {
    return keyAttributes(p, this.categories.value().find((c) => c.id === p.category));
  }

  protected unitLabel(unit: string): string {
    return unit === 'sqft' ? 'sq ft' : unit;
  }

  protected describe(err: unknown): string {
    return describeError(err as Parameters<typeof describeError>[0]);
  }
}
