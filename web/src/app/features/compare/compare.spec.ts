import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { Category, Product } from '../catalog/catalog.api';
import { CompareSelection, compareRows, estimateItems } from './compare';
import { ComparePage } from './compare.page';

const p = (sku: string, category: string, unit: string, listPrice: number, attributes: Record<string, string> = {}): Product => ({
  sku, name: sku, description: null, unit, listPrice, currency: 'USD', category, imageUrl: null, attributes,
});

const countertops: Category = {
  id: 'countertops', name: 'Countertops', defaultUnit: 'sqft', scene: 'kitchen',
  typical: { quantity: 30, description: 'an average kitchen (30 sq ft)' },
  attributes: [
    { key: 'material', label: 'Material', kind: 'choice', required: true, options: ['quartz', 'granite', 'butcher-block'], unit: null },
    { key: 'thicknessCm', label: 'Thickness', kind: 'number', required: false, options: null, unit: 'cm' },
  ],
};

describe('CompareSelection', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  it('keeps up to four products of one category', () => {
    const s = TestBed.inject(CompareSelection);
    for (const sku of ['A', 'B', 'C', 'D', 'E']) s.toggle({ sku, category: 'countertops' });
    expect(s.skus()).toEqual(['A', 'B', 'C', 'D']);
    expect(s.full()).toBe(true);
  });

  it('a product from another category starts a new comparison; toggling removes', () => {
    const s = TestBed.inject(CompareSelection);
    s.toggle({ sku: 'A', category: 'countertops' });
    s.toggle({ sku: 'B', category: 'countertops' });
    s.toggle({ sku: 'S', category: 'sheds' });
    expect([s.category(), s.skus()]).toEqual(['sheds', ['S']]);

    s.toggle({ sku: 'S', category: 'sheds' });
    expect([s.category(), s.skus()]).toEqual([null, []]);
  });

  it('survives a reload (per tab)', () => {
    TestBed.inject(CompareSelection).toggle({ sku: 'A', category: 'countertops' });
    TestBed.resetTestingModule();
    expect(TestBed.inject(CompareSelection).skus()).toEqual(['A']);
  });
});

describe('compare helpers', () => {
  it('one row per attribute, with units, and a dash where a product does not say', () => {
    expect(compareRows([p('A', 'countertops', 'sqft', 1, { material: 'butcher-block', thicknessCm: '4' }), p('B', 'countertops', 'sqft', 1, { material: 'quartz' })], countertops)).toEqual([
      { label: 'Material', values: ['butcher block', 'quartz'] },
      { label: 'Thickness (cm)', values: ['4', '—'] },
    ]);
  });

  it('asks for estimates only for products sold in the category default unit', () => {
    expect(estimateItems([p('A', 'countertops', 'sqft', 89), p('R', 'countertops', 'roll', 89)], countertops)).toEqual([{ sku: 'A', quantity: 30 }]);
    expect(estimateItems([p('A', 'countertops', 'sqft', 89)], { ...countertops, typical: null })).toEqual([]);
  });
});

describe('ComparePage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({ imports: [ComparePage], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    sessionStorage.clear();
  });

  it('shows the products side by side with server estimates and marks the lowest', async () => {
    const sel = TestBed.inject(CompareSelection);
    sel.toggle({ sku: 'Q', category: 'countertops' });
    sel.toggle({ sku: 'G', category: 'countertops' });

    const fixture = TestBed.createComponent(ComparePage);
    TestBed.tick();
    http.expectOne('/api/catalog/products').flush([p('Q', 'countertops', 'sqft', 89, { material: 'quartz' }), p('G', 'countertops', 'sqft', 72, { material: 'granite' }), p('X', 'sheds', 'each', 1)]);
    http.expectOne('/api/catalog/categories').flush([countertops]);
    // Let the flushed responses land, then run effects: the estimates request goes out.
    // (Answer it before whenStable(): a pending request keeps the page "unstable".)
    await new Promise((r) => setTimeout(r));
    TestBed.tick();

    const req = http.expectOne('/api/quotes/estimates');
    expect(req.request.body).toEqual({ items: [{ sku: 'Q', quantity: 30 }, { sku: 'G', quantity: 30 }] });
    req.flush([
      { sku: 'Q', quantity: 30, total: 2890.28, currency: 'USD', errors: [] },
      { sku: 'G', quantity: 30, total: 2338.2, currency: 'USD', errors: [] },
    ]);
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    const estimates = [...el.querySelectorAll('[data-testid="estimate"]')].map((e) => e.textContent?.trim());
    expect(estimates).toEqual(['$2,890.28', '$2,338.20']);
    expect(el.querySelector('th.best')?.textContent).toContain('G');
    expect(el.textContent).toContain('an average kitchen (30 sq ft)');
  });

  it('asks for at least two products when the selection is too small', async () => {
    TestBed.inject(CompareSelection).toggle({ sku: 'Q', category: 'countertops' });
    const fixture = TestBed.createComponent(ComparePage);
    TestBed.tick();
    http.match('/api/catalog/products').forEach((r) => r.flush([]));
    http.match('/api/catalog/categories').forEach((r) => r.flush([]));
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="compare-empty"]')).not.toBeNull();
  });
});
