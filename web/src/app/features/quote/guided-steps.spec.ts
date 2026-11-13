import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { GuidedStepsComponent } from './guided-steps.component';
import { GuidedQuote, ProductOptions } from './guided';

const OPTIONS: ProductOptions = {
  product: { sku: 'CT-QTZ', name: 'Quartz', description: null, unit: 'sqft', listPrice: 89, currency: 'USD', category: 'countertops', imageUrl: null, attributes: {} },
  measure: 'countertop',
  minimumQuantity: 25,
  groups: [
    { id: 'edge', step: 'options', label: 'Edge style', help: null, selection: 'single', basis: 'edgeFt', required: true, noneLabel: null,
      options: [{ sku: 'EDGE-OGEE', name: 'Ogee edge', unit: 'ft', price: 18, currency: 'USD', imageUrl: null, included: 0 }] },
    { id: 'sink', step: 'options', label: 'Sink', help: null, selection: 'quantity', basis: 'choice', required: true, noneLabel: 'No new sink',
      options: [{ sku: 'SINK-DBL', name: 'Double bowl sink', unit: 'each', price: 238, currency: 'USD', imageUrl: null, included: 0 }] },
    { id: 'plan', step: 'protection', label: 'Protection plan', help: null, selection: 'single', basis: 'fixed', required: true, noneLabel: 'No protection plan',
      options: [{ sku: 'PLAN-10Y', name: '10-year plan', unit: 'each', price: 399, currency: 'USD', imageUrl: null, included: 0 }] },
  ],
};

const quote = (missing: string[]): GuidedQuote => ({
  measurement: { kind: 'countertop', values: { sqft: 20.84, edgeFt: 10 }, productBasis: 'sqft' },
  lines: [
    { sku: 'CT-QTZ', name: 'Quartz', unit: 'sqft', quantity: 20.84, unitPrice: 89, lineTotal: 1854.76, group: 'product' },
    { sku: 'CT-QTZ-MIN', name: 'Minimum charge (25 sqft minimum)', unit: 'sqft', quantity: 4.16, unitPrice: 89, lineTotal: 370.24, group: 'minimum' },
  ],
  currency: 'USD', subtotal: 2225, taxRate: 0.0825, tax: 183.56, total: 2408.56, missing, complete: missing.length === 0,
});

describe('GuidedStepsComponent (2n-b..e)', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [GuidedStepsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  const settle = async () => {
    TestBed.tick();
    await new Promise((r) => setTimeout(r));
    TestBed.tick();
  };

  it('measures, answers each step, shows the grouped quote and saves when complete', async () => {
    const fixture = TestBed.createComponent(GuidedStepsComponent);
    fixture.componentRef.setInput('zip', '62701');
    fixture.componentRef.setInput('sku', 'CT-QTZ');
    const el = fixture.nativeElement as HTMLElement;
    await settle();

    http.expectOne('/api/catalog/products/CT-QTZ/options?zip=62701').flush(OPTIONS);
    await settle();

    // The default 10 ft x 25 in top is priced straight away; nothing answered yet.
    const first = http.expectOne('/api/quotes/guided/preview');
    expect(first.request.body).toEqual({
      zip: '62701', sku: 'CT-QTZ',
      measurements: { countertop: { layout: 'straight', countertop: [{ widthIn: 120, depthIn: 25 }], island: [], backsplash: [] } },
      choices: [],
    });
    first.flush(quote(['edge', 'sink', 'plan']));
    await settle();

    expect(el.querySelector('[data-testid="measured"]')?.textContent).toContain('20.84 sq ft');
    expect(el.querySelector('[data-testid="missing"]')?.textContent).toContain('Edge style, Sink, Protection plan');
    expect(el.querySelectorAll('.group-title')[1]?.textContent).toContain('Minimum charge');
    expect(el.querySelector<HTMLButtonElement>('[data-testid="save-guided"]')!.disabled).toBe(true);

    // Answer: ogee edge, one sink, no plan. Each answer re-prices.
    el.querySelector<HTMLInputElement>('[data-testid="opt-EDGE-OGEE"]')!.dispatchEvent(new Event('change'));
    await settle();
    http.expectOne('/api/quotes/guided/preview').flush(quote(['sink', 'plan']));
    await settle();

    el.querySelector<HTMLButtonElement>('[data-testid="more-SINK-DBL"]')!.click();
    await settle();
    http.expectOne('/api/quotes/guided/preview').flush(quote(['plan']));
    await settle();

    el.querySelector<HTMLInputElement>('[data-testid="none-plan"]')!.dispatchEvent(new Event('change'));
    await settle();
    const last = http.expectOne('/api/quotes/guided/preview');
    expect(last.request.body.choices).toEqual([
      { group: 'edge', sku: 'EDGE-OGEE' },
      { group: 'sink', sku: 'SINK-DBL', quantity: 1 },
      { group: 'plan', sku: null },
    ]);
    last.flush(quote([]));
    await settle();

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const save = el.querySelector<HTMLButtonElement>('[data-testid="save-guided"]')!;
    expect(save.disabled).toBe(false);
    save.click();
    await settle();
    const saved = http.expectOne((r) => r.url === '/api/quotes/guided' && r.method === 'POST');
    expect(saved.request.body.choices.length).toBe(3);
    saved.flush({ id: 'q-1' });
    await settle();
    expect(navigate).toHaveBeenCalledWith(['/quotes'], { queryParams: { saved: 'q-1' } });
  });

  it("shows the server's measurement problems next to the form", async () => {
    const fixture = TestBed.createComponent(GuidedStepsComponent);
    fixture.componentRef.setInput('zip', '62701');
    fixture.componentRef.setInput('sku', 'CT-QTZ');
    const el = fixture.nativeElement as HTMLElement;
    await settle();
    http.expectOne('/api/catalog/products/CT-QTZ/options?zip=62701').flush(OPTIONS);
    await settle();

    http.expectOne('/api/quotes/guided/preview').flush(
      { code: 'invalid_measurements', errors: [{ code: 'invalid_section', message: 'Countertop section 1: width must be 6-600 in.' }] },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();
    expect(el.querySelector('[data-testid="measure-error"]')?.textContent).toContain('width must be 6-600 in');
  });
});
