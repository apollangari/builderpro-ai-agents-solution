import { Component, computed, input, linkedSignal, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { describeError } from '../../core/api/describe-error';
import { Product, categoriesResource, productsResource } from '../catalog/catalog.api';
import { SceneComponent } from './scene.component';
import { PhotoOverlayComponent } from './photo-overlay.component';
import { sceneFor } from './scene';

/**
 * "See it in your home" (2l). Without a photo, the product is shown in its category's default
 * scene; the customer can flip through the other products of the same category in place.
 * "My photo" switches to the customer's own photo with 4-corner perspective (2l-b).
 */
@Component({
  selector: 'bp-visualize-page',
  imports: [CurrencyPipe, RouterLink, SceneComponent, PhotoOverlayComponent],
  template: `
    <div class="page">
      <header class="page-head">
        <span class="eyebrow">Visualize</span>
        <h1>{{ product()?.name ?? 'See it in your home' }}</h1>
        @if (category(); as c) {
          <p>Shown in a sample {{ c.scene.replace('-', ' ') }}. Pick another {{ c.name.toLowerCase() }} option below to compare looks.</p>
        }
      </header>

      @if (products.error()) {
        <div class="warning" role="alert"><span>{{ describe(products.error()) }}</span></div>
      } @else if (products.isLoading() && !product()) {
        <span class="skeleton" style="height: 18rem"></span>
      } @else if (!product() || !scene()) {
        <div class="card empty" data-testid="visualize-empty">
          <p>Choose a product in the catalog and select "See it in a room".</p>
          <a routerLink="/catalog" class="btn btn-primary">Go to the catalog</a>
        </div>
      } @else {
        <div class="layout">
          <div>
            <div class="modes" role="tablist" aria-label="How to view it">
              <button type="button" role="tab" class="tab" [class.active]="!usePhoto()" [attr.aria-selected]="!usePhoto()" (click)="usePhoto.set(false)" data-testid="mode-sample">Sample room</button>
              <button type="button" role="tab" class="tab" [class.active]="usePhoto()" [attr.aria-selected]="usePhoto()" (click)="usePhoto.set(true)" data-testid="mode-photo">My photo</button>
            </div>
            @if (usePhoto()) {
              <bp-photo-overlay [product]="product()!" />
            } @else {
              <bp-scene [scene]="scene()!" [product]="product()!" />
            }
          </div>
          <aside class="card side">
            <h2 class="card-title">{{ product()!.name }}</h2>
            <p class="price">{{ product()!.listPrice | currency: product()!.currency }} <span>{{ product()!.unit === 'each' ? 'each' : 'per ' + product()!.unit }}</span></p>
            <h3>Other options</h3>
            <div class="options" role="radiogroup" [attr.aria-label]="category()!.name + ' options'">
              @for (o of options(); track o.sku) {
                <button
                  type="button"
                  role="radio"
                  class="option"
                  [class.active]="o.sku === product()!.sku"
                  [attr.aria-checked]="o.sku === product()!.sku"
                  [attr.data-testid]="'option-' + o.sku"
                  (click)="selected.set(o.sku)"
                >
                  @if (o.imageUrl) {
                    <img [src]="o.imageUrl" alt="" width="400" height="300" />
                  }
                  <span>{{ o.name }}</span>
                </button>
              }
            </div>
            <a routerLink="/catalog" class="btn">Back to the catalog</a>
          </aside>
        </div>
      }
    </div>
  `,
  styles: `
    .modes { display: flex; gap: .4rem; margin-bottom: .75rem; }
    .tab {
      min-height: var(--bp-tap); padding: 0 1rem; border-radius: 999px; border: 1px solid var(--bp-border-strong);
      background: var(--bp-surface); color: var(--bp-ink); font: inherit; font-weight: 600; cursor: pointer;
    }
    .tab.active { background: var(--bp-accent); border-color: var(--bp-accent); color: var(--bp-on-accent); }
    .layout { display: grid; grid-template-columns: minmax(0, 1fr) 20rem; gap: 1.25rem; align-items: start; }
    @media (max-width: 900px) { .layout { grid-template-columns: 1fr; } }
    .side { display: grid; gap: .75rem; }
    .price { margin: 0; font-family: var(--bp-font-heading); font-size: 1.4rem; font-weight: 700; color: var(--bp-accent); }
    .price span { font-family: var(--bp-font-body); font-size: .85rem; font-weight: 500; color: var(--bp-ink-muted); }
    h3 { margin: .5rem 0 0; font-size: .85rem; color: var(--bp-ink-muted); text-transform: uppercase; letter-spacing: .06em; }
    .options { display: grid; gap: .5rem; }
    .option {
      display: flex; align-items: center; gap: .75rem; min-height: var(--bp-tap); padding: .4rem; text-align: left;
      border: 1px solid var(--bp-border-strong); border-radius: var(--bp-radius-md); background: var(--bp-surface);
      font: inherit; color: var(--bp-ink); cursor: pointer;
    }
    .option:hover { background: var(--bp-surface-muted); }
    .option.active { border: 2px solid var(--bp-accent); background: var(--bp-accent-soft); }
    .option img { width: 3.5rem; height: auto; aspect-ratio: 4 / 3; object-fit: cover; border-radius: 6px; flex: none; }
    .empty { text-align: center; padding: 2.5rem; }
    .empty p { color: var(--bp-ink-muted); margin: 0 0 1rem; }
  `,
})
export class VisualizePage {
  /** ?sku= from the URL (withComponentInputBinding). */
  readonly sku = input<string | undefined>();

  protected readonly products = productsResource();
  protected readonly categories = categoriesResource();
  protected readonly describe = describeError;

  /** Sample scene (default) or the customer's own photo (2l-b). */
  protected readonly usePhoto = signal(false);

  /** Starts as ?sku= (and resets when the URL changes), then follows the option buttons. */
  protected readonly selected = linkedSignal(() => this.sku());

  protected readonly product = computed<Product | undefined>(() => this.products.value().find((p) => p.sku === this.selected()));
  protected readonly category = computed(() => this.categories.value().find((c) => c.id === this.product()?.category));
  protected readonly scene = computed(() => sceneFor(this.category()));
  /** Same category, image first so the swatches look like a palette. */
  protected readonly options = computed(() => this.products.value().filter((p) => p.category === this.product()?.category));
}
