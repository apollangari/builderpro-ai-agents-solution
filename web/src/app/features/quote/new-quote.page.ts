import { Component, computed, inject, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { describeError } from '../../core/api/describe-error';
import { Product, categoriesResource } from '../catalog/catalog.api';
import { categoryTabs, keyAttributes } from '../catalog/picker';
import { GuidedStepsComponent } from './guided-steps.component';

/** GET /api/catalog/service-area answer. Mirrors ServiceAreaDto in C#: yes/no only, never the store's ZIP lists. */
interface ServiceArea {
  zip: string; // normalized by the server ("75201", "K1A 0B1")
  served: boolean;
}

/**
 * Guided quote, step 1 (2n-a): confirm the project ZIP, pick the project type, pick a product.
 *
 *  - The ZIP starts as the one from the customer's sign-up address (the postal_code claim);
 *    "No" asks for the project's ZIP. Nothing is written to the profile.
 *  - Products come from GET /api/catalog/products?zip=…: only what the store sells in that ZIP,
 *    at that ZIP's price. The page never computes or adjusts a price.
 *  - Signal-first: the service-area check and the product list are httpResources driven by signals.
 */
@Component({
  selector: 'bp-new-quote-page',
  imports: [CurrencyPipe, GuidedStepsComponent],
  template: `
    <div class="page">
      <header class="page-head">
        <span class="eyebrow">New quote</span>
        <h1>Start your project</h1>
        <p>Tell us where the project is and what you're working on. Prices are for your area.</p>
      </header>

      <ol class="steps" aria-label="Progress">
        <li [class.done]="served()" [class.current]="!served()">ZIP code</li>
        <li [class.done]="!!category()" [class.current]="served() && !category()">Project type</li>
        <li [class.done]="!!selected()" [class.current]="!!category() && !selected()">Product</li>
        <li [class.current]="!!selected()">Measure</li>
        <li>Options</li>
        <li>Protection</li>
        <li>Quote</li>
      </ol>

      <!-- Step 1: ZIP -->
      @if (!served()) {
        <section class="card step" data-testid="step-zip">
          @if (profileZip() && !editing() && !zip()) {
            <h2 class="card-title">Is this project at ZIP {{ profileZip() }}?</h2>
            <p class="muted">That's the ZIP from your account.</p>
            <div class="actions">
              <button type="button" class="btn btn-primary" (click)="useZip(profileZip()!)" data-testid="zip-yes">Yes</button>
              <button type="button" class="btn" (click)="editing.set(true)" data-testid="zip-no">No, it's somewhere else</button>
            </div>
          } @else {
            <form (submit)="$event.preventDefault(); useZip(zipInput())">
              <label class="field-label" for="zip-input">ZIP code of the project</label>
              <div class="row">
                <input id="zip-input" type="text" inputmode="text" autocomplete="postal-code" maxlength="10"
                       [value]="zipInput()" (input)="zipInput.set($any($event.target).value)" data-testid="zip-input" />
                <button type="submit" class="btn btn-primary" [disabled]="!zipInput().trim() || area.isLoading()" data-testid="zip-continue">Continue</button>
              </div>
            </form>
          }

          @if (area.isLoading()) {
            <p class="muted" aria-live="polite">Checking your area…</p>
          } @else if (area.hasValue() && !area.value()!.served) {
            <div class="warning" role="alert" data-testid="zip-not-served">
              <span>We don't serve ZIP {{ area.value()!.zip }} yet. Try the ZIP where the work will happen.</span>
            </div>
          } @else if (area.error()) {
            <div class="warning" role="alert" data-testid="zip-error"><span>{{ describe(area.error()) }}</span></div>
          }
        </section>
      } @else {
        <p class="chosen" data-testid="zip-chosen">
          Project ZIP <strong>{{ servedZip() }}</strong>
          <button type="button" class="link" (click)="changeZip()" data-testid="zip-change">Change</button>
        </p>

        <!-- Step 2: project type (only types the store sells in this ZIP) -->
        <section class="card step" data-testid="step-category">
          <h2 class="card-title">What are you working on?</h2>
          @if (products.isLoading()) {
            <p class="muted">Loading products for your area…</p>
          } @else if (products.error()) {
            <div class="warning" role="alert"><span>{{ describe(products.error()) }}</span></div>
          } @else if (types().length === 0) {
            <p class="muted" data-testid="no-products">Your store has no products for this ZIP yet.</p>
          } @else {
            <div class="tiles">
              @for (t of types(); track t.id) {
                <button type="button" class="tile" [class.active]="category() === t.id" [attr.aria-pressed]="category() === t.id"
                        (click)="pickCategory(t.id!)" [attr.data-testid]="'type-' + t.id">
                  @if (pictureOf(t.id!); as pic) { <img [src]="pic" alt="" width="320" height="200" loading="lazy" /> }
                  <span class="tile-name">{{ t.name }}</span>
                  <span class="muted">{{ t.count }} {{ t.count === 1 ? 'option' : 'options' }}</span>
                </button>
              }
            </div>
          }
        </section>

        <!-- Step 3: product, at this ZIP's price -->
        @if (category()) {
          <section class="card step" data-testid="step-product">
            <h2 class="card-title">Choose a product</h2>
            <div class="grid">
              @for (p of inCategory(); track p.sku) {
                <article class="product" [class.active]="selected() === p.sku" [attr.data-testid]="'product-' + p.sku">
                  @if (p.imageUrl) { <img [src]="p.imageUrl" alt="" width="320" height="200" loading="lazy" /> }
                  <h3>{{ p.name }}</h3>
                  <div class="chips">
                    @for (a of badges(p); track a) { <span class="badge">{{ a }}</span> }
                  </div>
                  <p class="price" data-testid="price">{{ p.listPrice | currency: p.currency }} <span class="muted">/ {{ unitLabel(p.unit) }}</span></p>
                  <button type="button" class="btn" [class.btn-primary]="selected() === p.sku" (click)="selected.set(p.sku)"
                          [attr.aria-pressed]="selected() === p.sku">{{ selected() === p.sku ? 'Selected' : 'Choose' }}</button>
                </article>
              }
            </div>
          </section>
        }

        <!-- Steps 4-7: measurements, options, protection, quote (fresh for each product) -->
        @for (c of chosenList(); track c.sku) {
          <bp-guided-steps [zip]="servedZip()!" [sku]="c.sku" />
        }
      }
    </div>
  `,
  styles: `
    .steps { display: flex; flex-wrap: wrap; gap: .5rem; list-style: none; padding: 0; margin: 0 0 1rem; counter-reset: step; }
    .steps li { counter-increment: step; padding: .35rem .8rem; border-radius: 999px; border: 1px solid var(--bp-border); color: var(--bp-ink-muted); font-size: .9rem; }
    .steps li::before { content: counter(step) '. '; }
    .steps li.current { border-color: var(--bp-accent); color: var(--bp-accent); font-weight: 600; }
    .steps li.done { background: var(--bp-accent-soft); color: var(--bp-accent); border-color: transparent; }
    .step { display: grid; gap: .75rem; margin-bottom: 1rem; }
    .row { display: flex; gap: .6rem; flex-wrap: wrap; }
    .field-label { display: block; font-weight: 600; margin-bottom: .35rem; }
    input[type='text'] {
      min-height: var(--bp-tap); padding: 0 .9rem; border-radius: var(--bp-radius-md); width: 12rem;
      border: 1px solid var(--bp-border-strong); background: var(--bp-surface); color: var(--bp-ink); font: inherit;
    }
    .actions { display: flex; gap: .6rem; flex-wrap: wrap; }
    .chosen { display: flex; align-items: center; gap: .6rem; margin: 0 0 1rem; }
    .link { min-height: var(--bp-tap); background: none; border: 0; color: var(--bp-accent); text-decoration: underline; cursor: pointer; font: inherit; }
    .tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(10rem, 1fr)); gap: .75rem; }
    .tile {
      display: grid; gap: .2rem; text-align: left; min-height: 4.5rem; padding: .8rem 1rem; cursor: pointer; font: inherit; color: var(--bp-ink);
      border: 1px solid var(--bp-border); border-radius: var(--bp-radius-md); background: var(--bp-surface);
    }
    .tile img { width: 100%; height: auto; aspect-ratio: 16 / 10; object-fit: cover; border-radius: var(--bp-radius-sm, 6px); }
    .tile.active { border-color: var(--bp-accent); box-shadow: 0 0 0 2px var(--bp-accent-soft); }
    .tile-name { font-weight: 600; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(14rem, 1fr)); gap: 1rem; }
    .product { display: grid; gap: .5rem; align-content: start; padding: .75rem; border: 1px solid var(--bp-border); border-radius: var(--bp-radius-md); }
    .product.active { border-color: var(--bp-accent); box-shadow: 0 0 0 2px var(--bp-accent-soft); }
    .product img { width: 100%; height: auto; border-radius: var(--bp-radius-sm, 6px); }
    .product h3 { margin: 0; font-size: 1rem; }
    .price { margin: 0; font-weight: 700; }
  `,
})
export class NewQuotePage {
  private readonly oidc = inject(OidcSecurityService);

  /** The ZIP from the sign-up address (ID token claim postal_code), or null. */
  protected readonly profileZip = computed(() => {
    const claims = this.oidc.userData().userData as { postal_code?: string } | null;
    return claims?.postal_code?.trim() || null;
  });

  protected readonly editing = signal(false);
  protected readonly zipInput = signal('');
  /** The ZIP being checked or in use; null until the customer answers. */
  protected readonly zip = signal<string | null>(null);
  protected readonly category = signal<string | null>(null);
  protected readonly selected = signal<string | null>(null);

  protected readonly area = httpResource<ServiceArea>(() => {
    const zip = this.zip();
    return zip ? `/api/catalog/service-area?zip=${encodeURIComponent(zip)}` : undefined;
  });
  protected readonly served = computed(() => this.area.hasValue() && this.area.value()!.served);
  protected readonly servedZip = computed(() => (this.served() ? this.area.value()!.zip : null));

  /** Only products sold in this ZIP, at this ZIP's price. */
  protected readonly products = httpResource<Product[]>(
    () => {
      const zip = this.servedZip();
      return zip ? `/api/catalog/products?zip=${encodeURIComponent(zip)}` : undefined;
    },
    { defaultValue: [] },
  );
  private readonly categories = categoriesResource();

  /** Project types: categories sold in this ZIP that are projects (labour and "other" have no scene and aren't picked on their own). */
  protected readonly types = computed(() => {
    const projects = this.categories.value().filter((c) => c.scene !== 'none');
    return categoryTabs(this.products.value(), projects).filter((t) => t.id !== null);
  });
  protected readonly inCategory = computed(() => this.products.value().filter((p) => p.category === this.category()));
  protected readonly chosen = computed(() => this.inCategory().find((p) => p.sku === this.selected()) ?? null);
  /** 0 or 1 items, so the steps component is created fresh when the product changes. */
  protected readonly chosenList = computed(() => (this.chosen() ? [this.chosen()!] : []));

  /** A project type's picture: a product of that type sold in this ZIP. */
  protected pictureOf(category: string): string | null {
    return this.products.value().find((p) => p.category === category && p.imageUrl)?.imageUrl ?? null;
  }

  protected useZip(zip: string): void {
    const value = zip.trim();
    if (!value) return;
    this.category.set(null);
    this.selected.set(null);
    this.zip.set(value);
  }

  protected changeZip(): void {
    this.zipInput.set('');
    this.zip.set(null);
    this.editing.set(true);
    this.category.set(null);
    this.selected.set(null);
  }

  protected pickCategory(id: string): void {
    this.category.set(id);
    this.selected.set(null);
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
