import { Component, computed, effect, inject, signal } from '@angular/core';
import { CurrencyPipe, DecimalPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Router, RouterLink } from '@angular/router';
import { describeError } from '../../core/api/describe-error';
import { Product, productsResource } from '../catalog/catalog.api';
import { QuoteDraft, RoofQuantities, roofLines } from './quote-draft';

/** POST /api/quotes and /api/quotes/preview answer. Mirrors QuoteDto in C#. */
export interface QuoteDto {
  id: string;
  status: string;
  currency: string;
  lines: { sku: string; name: string; unit: string; quantity: number; unitPrice: number; lineTotal: number }[];
  subtotal: number;
  taxRate: number;
  tax: number;
  total: number;
  createdAt: string;
}

/**
 * Quote builder (2m). The draft holds SKUs and quantities; every price on this page comes from
 * POST /api/quotes/preview, which prices exactly like submitting does. The roof calculator asks
 * POST /api/quotes/estimators/roof for quantities. Signal-first: requests are httpResources driven
 * by signals (the preview re-prices whenever the draft changes; submit fires when its body is set).
 */
@Component({
  selector: 'bp-quote-page',
  imports: [CurrencyPipe, DecimalPipe, RouterLink],
  template: `
    <div class="page">
      <header class="page-head">
        <span class="eyebrow">Quote</span>
        <h1>Your quote</h1>
        <p>Add products from the catalog or use the roof calculator. Prices and totals come from your store.</p>
      </header>

      <div class="layout">
        <section class="card">
          <h2 class="card-title">Lines</h2>
          @if (draft.count() === 0) {
            <p class="muted" data-testid="quote-empty">Nothing here yet. <a routerLink="/catalog">Browse the catalog</a> and choose "Add to quote".</p>
          } @else {
            <table class="lines">
              <thead><tr><th scope="col">Product</th><th scope="col">Quantity</th><th scope="col" class="num">Line total</th><th scope="col"><span class="sr-only">Remove</span></th></tr></thead>
              <tbody>
                @for (l of draft.lines(); track l.sku) {
                  <tr data-testid="quote-line">
                    <td>{{ nameOf(l.sku) }} <span class="muted">{{ unitOf(l.sku) }}</span></td>
                    <td>
                      <input type="number" min="0" step="any" class="qty" [value]="l.quantity" [attr.aria-label]="'Quantity of ' + nameOf(l.sku)"
                             [attr.data-testid]="'qty-' + l.sku" (change)="draft.set(l.sku, +$any($event.target).value)" />
                    </td>
                    <td class="num" data-testid="line-total">{{ lineTotal(l.sku) != null ? (lineTotal(l.sku)! | currency: currency()) : '…' }}</td>
                    <td><button type="button" class="btn" (click)="draft.remove(l.sku)" [attr.aria-label]="'Remove ' + nameOf(l.sku)">Remove</button></td>
                  </tr>
                }
              </tbody>
            </table>

            @if (preview.error()) {
              <div class="warning" role="alert" data-testid="preview-error"><span>{{ previewProblem() }}</span></div>
            } @else if (preview.hasValue()) {
              @let p = preview.value()!;
              <dl class="totals" data-testid="totals">
                <dt>Subtotal</dt><dd>{{ p.subtotal | currency: p.currency }}</dd>
                <dt>Tax ({{ p.taxRate * 100 | number: '1.0-3' }}%)</dt><dd>{{ p.tax | currency: p.currency }}</dd>
                <dt class="total">Total</dt><dd class="total" data-testid="total">{{ p.total | currency: p.currency }}</dd>
              </dl>
            }

            <div class="actions">
              <button type="button" class="btn btn-primary" (click)="submit()" [disabled]="!canSubmit()" data-testid="submit-quote">
                {{ submitting() ? 'Saving…' : 'Save quote' }}
              </button>
              <button type="button" class="btn" (click)="draft.clear()">Clear</button>
            </div>
            @if (submitted.error()) {
              <div class="warning" role="alert" data-testid="submit-error"><span>{{ describe(submitted.error()) }}</span></div>
            }
          }
        </section>

        <section class="card roof">
          <h2 class="card-title">Roof calculator</h2>
          <p class="muted">Enter your roof and we'll add shingles, underlayment, drip edge and labour.</p>
          <div class="form">
            <label>Footprint (sq ft)<input type="number" min="1" [value]="footprint()" (input)="footprint.set(+$any($event.target).value)" data-testid="roof-footprint" /></label>
            <label>Pitch (rise per 12 in)<input type="number" min="0" max="24" [value]="pitch()" (input)="pitch.set(+$any($event.target).value)" data-testid="roof-pitch" /></label>
            <label>Waste %<input type="number" min="0" max="30" [value]="waste()" (input)="waste.set(+$any($event.target).value)" /></label>
            <label>Eaves + rakes (ft)<input type="number" min="0" [value]="edges()" (input)="edges.set(+$any($event.target).value)" /></label>
            <label>Shingles
              <select (change)="shingle.set($any($event.target).value)" [value]="shingle()" data-testid="roof-shingle">
                @for (s of shingles(); track s.sku) { <option [value]="s.sku">{{ s.name }}</option> }
              </select>
            </label>
          </div>
          <button type="button" class="btn" (click)="calculate()" data-testid="roof-calc">Calculate</button>

          @if (roof.error()) {
            <div class="warning" role="alert"><span>Check the roof numbers: footprint above 0, pitch 0–24, waste 0–30 %.</span></div>
          } @else if (roof.hasValue()) {
            @let q = roof.value()!;
            <dl class="totals" data-testid="roof-result">
              <dt>Roof area</dt><dd>{{ q.roofAreaSqFt | number: '1.0-1' }} sq ft</dd>
              <dt>Squares (with waste)</dt><dd>{{ q.squares }}</dd>
              <dt>Shingle bundles</dt><dd>{{ q.shingleBundles }}</dd>
              <dt>Underlayment rolls</dt><dd>{{ q.underlaymentRolls }}</dd>
              <dt>Drip edge pieces</dt><dd>{{ q.dripEdgePieces }}</dd>
            </dl>
            <button type="button" class="btn btn-primary" (click)="addRoof(q)" data-testid="roof-add">Add roof to quote</button>
            @if (missing().length) {
              <p class="muted">Your store doesn't sell: {{ missing().join(', ') }}.</p>
            }
          }
        </section>
      </div>
    </div>
  `,
  styles: `
    .layout { display: grid; grid-template-columns: minmax(0, 1fr) 22rem; gap: 1.25rem; align-items: start; }
    @media (max-width: 900px) { .layout { grid-template-columns: 1fr; } }
    .muted { color: var(--bp-ink-muted); font-size: .9rem; }
    .lines { width: 100%; border-collapse: collapse; margin: .5rem 0 1rem; }
    .lines th, .lines td { padding: .5rem .4rem; border-bottom: 1px solid var(--bp-border); text-align: left; vertical-align: middle; }
    .lines th { font-size: .8rem; color: var(--bp-ink-muted); }
    .num { text-align: right !important; white-space: nowrap; }
    .qty, .form input, .form select {
      min-height: var(--bp-tap); width: 7rem; padding: 0 .6rem; border-radius: var(--bp-radius-md);
      border: 1px solid var(--bp-border-strong); background: var(--bp-surface); color: var(--bp-ink); font: inherit;
    }
    .totals { display: grid; grid-template-columns: 1fr auto; gap: .4rem 1rem; margin: .75rem 0; }
    .totals dt { color: var(--bp-ink-muted); }
    .totals dd { margin: 0; text-align: right; font-weight: 600; }
    .totals .total { font-family: var(--bp-font-heading); font-size: 1.3rem; color: var(--bp-accent); }
    .actions { display: flex; gap: .6rem; flex-wrap: wrap; }
    .form { display: grid; gap: .6rem; margin: .75rem 0; }
    .form label { display: grid; gap: .25rem; font-size: .8rem; font-weight: 600; color: var(--bp-ink-muted); }
    .form input, .form select { width: 100%; }
  `,
})
export class QuotePage {
  protected readonly draft = inject(QuoteDraft);
  private readonly router = inject(Router);
  protected readonly products = productsResource();
  protected readonly describe = describeError;

  private readonly bySku = computed(() => new Map(this.products.value().map((p) => [p.sku, p])));
  protected nameOf(sku: string): string { return this.bySku().get(sku)?.name ?? sku; }
  protected unitOf(sku: string): string {
    const u = this.bySku().get(sku)?.unit;
    return u ? (u === 'each' ? 'each' : `per ${u}`) : '';
  }

  // ---------- live preview of the whole quote ----------
  protected readonly preview = httpResource<QuoteDto>(() =>
    this.draft.count() ? { url: '/api/quotes/preview', method: 'POST', body: { lines: this.draft.lines() } } : undefined,
  );
  protected readonly currency = computed(() => this.preview.value()?.currency ?? 'USD');
  private readonly lineTotals = computed(() => new Map((this.preview.value()?.lines ?? []).map((l) => [l.sku, l.lineTotal])));
  protected lineTotal(sku: string): number | null { return this.lineTotals().get(sku) ?? null; }
  /** 400 invalid_quote carries per-line codes; show the first one in words. */
  protected readonly previewProblem = computed(() => {
    const err = this.preview.error() as { error?: { errors?: { message: string }[] } } | undefined;
    return err?.error?.errors?.[0]?.message ?? describeError(this.preview.error());
  });

  // ---------- submit ----------
  private readonly submitBody = signal<{ lines: { sku: string; quantity: number }[] } | null>(null);
  protected readonly submitted = httpResource<QuoteDto>(() =>
    this.submitBody() ? { url: '/api/quotes', method: 'POST', body: this.submitBody() } : undefined,
  );
  protected readonly submitting = computed(() => this.submitted.isLoading());
  protected readonly canSubmit = computed(() => this.draft.count() > 0 && this.preview.hasValue() && !this.preview.error() && !this.submitting());

  submit(): void {
    this.submitBody.set({ lines: [...this.draft.lines()] });
  }

  // ---------- roof calculator ----------
  protected readonly footprint = signal(2000);
  protected readonly pitch = signal(6);
  protected readonly waste = signal(10);
  protected readonly edges = signal(180);
  protected readonly shingles = computed<Product[]>(() =>
    this.products.value().filter((p) => p.category === 'roofing' && p.attributes['type'] === 'shingle'),
  );
  protected readonly shingle = signal('');
  private readonly roofRequest = signal<{ footprintSqFt: number; pitchRise: number; wastePercent: number; eavesAndRakesFt: number } | null>(null);
  protected readonly roof = httpResource<RoofQuantities>(() =>
    this.roofRequest() ? { url: '/api/quotes/estimators/roof', method: 'POST', body: this.roofRequest() } : undefined,
  );
  protected readonly missing = signal<string[]>([]);

  calculate(): void {
    this.roofRequest.set({ footprintSqFt: this.footprint(), pitchRise: this.pitch(), wastePercent: this.waste(), eavesAndRakesFt: this.edges() });
  }

  addRoof(q: RoofQuantities): void {
    const { lines, missing } = roofLines(q, this.products.value(), this.shingle() || this.shingles()[0]?.sku || '');
    lines.forEach((l) => this.draft.add(l.sku, l.quantity));
    this.missing.set(missing);
  }

  constructor() {
    // Saved: forget the draft and show it in "My quotes".
    effect(() => {
      const saved = this.submitted.value();
      if (saved?.id) {
        this.draft.clear();
        void this.router.navigate(['/quotes'], { queryParams: { saved: saved.id } });
      }
    });
  }
}
