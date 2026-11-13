import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { WorkspacePage } from './workspace.page';
import { Project } from '../quote/guided';

const preview = {
  measurement: { kind: 'countertop' as const, values: { sqft: 55.5, edgeFt: 36 }, productBasis: 'sqft' },
  lines: [
    { sku: 'CT-QTZ', name: 'Quartz countertop, Calacatta white', unit: 'sqft', quantity: 55.5, unitPrice: 89, lineTotal: 4939.5, group: 'product' as const },
    { sku: 'EDGE-OGEE', name: 'Ogee edge', unit: 'ft', quantity: 36, unitPrice: 18, lineTotal: 648, group: 'options' as const },
  ],
  currency: 'USD', subtotal: 5587.5, taxRate: 0.0825, tax: 460.97, total: 6048.47, missing: [], complete: true,
};
const answers = { measurements: { countertop: { layout: 'l-island', countertop: [{ widthIn: 120, depthIn: 25 }, { widthIn: 96, depthIn: 25 }], island: [{ widthIn: 72, depthIn: 36 }], backsplash: [] } }, choices: [] };
const project = (over: Partial<Project> = {}): Project => ({
  id: 'p1', title: 'Countertops · Quartz', zip: '62701', stage: 'configure', sku: null, answers, preview, previewErrors: [], quote: null,
  updatedAt: '2026-11-13T09:00:00Z', ...over,
});
const issued = { id: 'q1', number: 1042, status: 'issued' as const, validUntil: '2026-11-27T09:00:00Z', acceptedAt: null, current: true, expired: false,
  currency: 'USD', lines: preview.lines, subtotal: 5587.5, taxRate: 0.0825, tax: 460.97, total: 6048.47 };

describe('WorkspacePage (2n-f)', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [WorkspacePage], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  const settle = async () => {
    TestBed.tick();
    await new Promise((r) => setTimeout(r));
    TestBed.tick();
  };

  async function open(p: Project) {
    const fixture = TestBed.createComponent(WorkspacePage);
    fixture.componentRef.setInput('id', 'p1');
    await settle();
    http.expectOne('/api/projects/p1').flush(p);
    await settle();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the stage rail, the estimated layout and gets the numbered quote', async () => {
    const el = await open(project());

    const track = [...el.querySelectorAll('[data-testid="tracker"] li')].map((li) => li.textContent?.replace(/\s+/g, ' ').trim());
    expect(track).toEqual(['✓ZIP', '✓Type', '✓Product', 'Measure', 'Quote', 'Deposit', 'Visit', 'Install']);
    expect(el.querySelector('[data-testid="tracker"] [aria-current="step"]')?.textContent).toContain('Measure');
    expect(el.querySelector('[data-testid="layout-card"]')?.textContent).toContain('Estimated');
    expect(el.querySelectorAll('[data-testid="layout-card"] rect').length).toBe(3);
    expect(el.querySelector('[data-testid="layout-summary"]')?.textContent).toContain('55.5 sq ft');

    // One screen at a time: Measure shows the sketch, not the quote.
    expect(el.querySelector('[data-testid="quote-card"]')).toBeNull();
    el.querySelector<HTMLButtonElement>('[data-testid="track-quote"] button')!.click();
    await settle();
    expect(el.querySelector('[data-testid="page-next"]')).toBeNull(); // last screen

    el.querySelector<HTMLButtonElement>('[data-testid="get-quote"]')!.click();
    await settle();
    http.expectOne((r) => r.url === '/api/projects/p1/quote' && r.method === 'POST').flush(project({ stage: 'quote', quote: issued }));
    await settle();

    expect(el.querySelector('[data-testid="quote-number"]')?.textContent).toContain('Q-1042');
    expect(el.querySelector('[data-testid="quote-total"]')?.textContent).toContain('$6,048.47');
    expect(el.querySelector('[data-testid="accept-quote"]')).not.toBeNull();
  });

  it('the customer accepts; the rail moves to Deposit', async () => {
    const el = await open(project({ stage: 'quote', quote: issued }));
    el.querySelector<HTMLButtonElement>('[data-testid="accept-quote"]')!.click();
    await settle();
    http.expectOne((r) => r.url === '/api/projects/p1/accept' && r.method === 'POST')
      .flush(project({ stage: 'deposit', quote: { ...issued, status: 'accepted', acceptedAt: '2026-11-13T10:00:00Z' } }));
    await settle();

    expect(el.querySelector('[data-testid="accepted"]')?.textContent).toContain('deposit is the next step');
    expect(el.querySelector('[data-testid="tracker"] [aria-current="step"]')?.textContent).toContain('Deposit');
    expect(el.querySelector('[data-testid="accept-quote"]')).toBeNull();
  });

  it('an outdated quote offers a new one instead of Accept', async () => {
    const el = await open(project({ stage: 'quote', quote: { ...issued, current: false } }));
    expect(el.querySelector('[data-testid="outdated"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="accept-quote"]')).toBeNull();
  });
});
