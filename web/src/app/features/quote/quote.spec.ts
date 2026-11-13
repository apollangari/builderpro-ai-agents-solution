import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { Product } from '../catalog/catalog.api';
import { QuoteDraft, roofLines } from './quote-draft';
import { QuotePage } from './quote.page';

const p = (sku: string, category: string, unit: string, attributes: Record<string, string> = {}): Product => ({
  sku, name: sku, description: null, unit, listPrice: 1, currency: 'USD', category, imageUrl: null, attributes,
});

const store = [
  p('SH-ARCH', 'roofing', 'bundle', { type: 'shingle' }),
  p('SH-3TAB', 'roofing', 'bundle', { type: 'shingle' }),
  p('UNDER', 'roofing', 'roll', { type: 'underlayment' }),
  p('DRIP', 'roofing', 'each', { type: 'flashing' }),
  p('LAB-ROOF', 'labour', 'square', { trade: 'roofing' }),
  p('LAB-HR', 'labour', 'hour', { trade: 'general' }),
];

describe('QuoteDraft', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  it('one line per SKU; adding again adds quantity; 0 removes', () => {
    const d = TestBed.inject(QuoteDraft);
    d.add('A');
    d.add('A', 2.5);
    d.add('B', 0.1);
    d.add('B', 0.2);
    expect(d.lines()).toEqual([{ sku: 'A', quantity: 3.5 }, { sku: 'B', quantity: 0.3 }]); // no 0.30000000000000004
    d.set('A', 0);
    expect(d.lines()).toEqual([{ sku: 'B', quantity: 0.3 }]);
  });

  it('survives a reload and ignores garbage in storage', () => {
    TestBed.inject(QuoteDraft).add('A', 2);
    TestBed.resetTestingModule();
    expect(TestBed.inject(QuoteDraft).lines()).toEqual([{ sku: 'A', quantity: 2 }]);

    sessionStorage.setItem('bp.quoteDraft', '[{"sku":"X","quantity":-1},{"nope":1},"x"]');
    TestBed.resetTestingModule();
    expect(TestBed.inject(QuoteDraft).lines()).toEqual([]);
  });
});

describe('roofLines', () => {
  const q = { pitchFactor: 1.118, roofAreaSqFt: 2236.1, squares: 24.6, shingleBundles: 74, underlaymentRolls: 3, dripEdgePieces: 18 };

  it("uses the store's own products, found by attributes", () => {
    expect(roofLines(q, store, 'SH-3TAB')).toEqual({
      lines: [
        { sku: 'SH-3TAB', quantity: 74 },
        { sku: 'UNDER', quantity: 3 },
        { sku: 'DRIP', quantity: 18 },
        { sku: 'LAB-ROOF', quantity: 24.6 },
      ],
      missing: [],
    });
  });

  it("reports what the store doesn't sell instead of guessing", () => {
    const r = roofLines(q, store.filter((x) => x.sku !== 'DRIP' && x.sku !== 'LAB-ROOF'), 'SH-ARCH');
    expect(r.missing).toEqual(['drip edge', 'roofing labour']);
  });
});

describe('QuotePage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({ imports: [QuotePage], providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    sessionStorage.clear();
  });

  const settle = async () => {
    await new Promise((r) => setTimeout(r));
    TestBed.tick();
  };

  it('previews the whole draft with server totals, then saves it and goes to My quotes', async () => {
    const draft = TestBed.inject(QuoteDraft);
    draft.add('SH-ARCH', 74);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    const fixture = TestBed.createComponent(QuotePage);
    TestBed.tick();
    http.expectOne('/api/catalog/products').flush(store);
    const preview = http.expectOne('/api/quotes/preview');
    expect(preview.request.body).toEqual({ lines: [{ sku: 'SH-ARCH', quantity: 74 }] });
    preview.flush({ id: '00000000-0000-0000-0000-000000000000', status: 'preview', currency: 'USD', lines: [{ sku: 'SH-ARCH', name: 'SH-ARCH', unit: 'bundle', quantity: 74, unitPrice: 42.97, lineTotal: 3179.78 }], subtotal: 3179.78, taxRate: 0.0825, tax: 262.33, total: 3442.11, createdAt: '' });
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="total"]')?.textContent).toContain('$3,442.11');
    expect(el.querySelector('[data-testid="line-total"]')?.textContent).toContain('$3,179.78');

    el.querySelector<HTMLButtonElement>('[data-testid="submit-quote"]')!.click();
    await settle();
    const save = http.expectOne((r) => r.url === '/api/quotes' && r.method === 'POST');
    expect(save.request.body).toEqual({ lines: [{ sku: 'SH-ARCH', quantity: 74 }] });
    save.flush({ id: 'q-1', status: 'draft', currency: 'USD', lines: [], subtotal: 0, taxRate: 0, tax: 0, total: 3442.11, createdAt: '' });
    await settle();

    expect(navigate).toHaveBeenCalledWith(['/quotes'], { queryParams: { saved: 'q-1' } });
    expect(draft.count()).toBe(0);
  });

  it('roof calculator: quantities from the server, added as lines', async () => {
    const fixture = TestBed.createComponent(QuotePage);
    TestBed.tick();
    http.expectOne('/api/catalog/products').flush(store);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="quote-empty"]')).not.toBeNull();

    el.querySelector<HTMLButtonElement>('[data-testid="roof-calc"]')!.click();
    await settle();
    const calc = http.expectOne('/api/quotes/estimators/roof');
    expect(calc.request.body).toEqual({ footprintSqFt: 2000, pitchRise: 6, wastePercent: 10, eavesAndRakesFt: 180 });
    calc.flush({ pitchFactor: 1.118, roofAreaSqFt: 2236.1, squares: 24.6, shingleBundles: 74, underlaymentRolls: 3, dripEdgePieces: 18 });
    await settle();

    el.querySelector<HTMLButtonElement>('[data-testid="roof-add"]')!.click();
    await settle();
    expect(TestBed.inject(QuoteDraft).lines().map((l) => `${l.sku}:${l.quantity}`)).toEqual(['SH-ARCH:74', 'UNDER:3', 'DRIP:18', 'LAB-ROOF:24.6']);
    http.expectOne('/api/quotes/preview').flush({ id: '', status: 'preview', currency: 'USD', lines: [], subtotal: 0, taxRate: 0, tax: 0, total: 0, createdAt: '' });
    await fixture.whenStable();
  });
});
