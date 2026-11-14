import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { WorkspacePage } from './workspace.page';
import { Project } from '../quote/guided';
import { signal } from '@angular/core';
import { StoreFeatures } from '../../core/tenant/store-features';
/** The store's package for the page (1h), without asking the server. */
function fakeFeatures(concierge = true, help = true) {
  return { concierge: signal(concierge), helpChat: signal(help), anyChat: signal(concierge || help), known: signal(true), features: signal([]),
    scopes: signal<string[]>([]), isCustomer: signal(true), salesperson: signal(false), salesDesk: signal(false) };
}


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

  let pkg: ReturnType<typeof fakeFeatures>;

  beforeEach(() => {
    pkg = fakeFeatures();
    TestBed.configureTestingModule({
      imports: [WorkspacePage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: StoreFeatures, useValue: pkg }],
    });
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

  it('after Accept the price is not held until the 5% deposit is paid; Pay opens the payment page', async () => {
    const el = await open(project({ stage: 'quote', quote: issued }));
    el.querySelector<HTMLButtonElement>('[data-testid="accept-quote"]')!.click();
    await settle();
    http.expectOne((r) => r.url === '/api/projects/p1/accept' && r.method === 'POST')
      .flush(project({ stage: 'deposit', quote: { ...issued, status: 'accepted', acceptedAt: '2026-11-13T10:00:00Z' } }));
    await settle();

    expect(el.querySelector('[data-testid="accepted"]')?.textContent).toContain('Pay a 5% deposit to hold this price');
    expect(el.querySelector('[data-testid="tracker"] [aria-current="step"]')?.textContent).toContain('Deposit');
    expect(el.querySelector('[data-testid="accept-quote"]')).toBeNull();

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    el.querySelector<HTMLButtonElement>('[data-testid="pay-deposit"]')!.click();
    await settle();
    http.expectOne((r) => r.url === '/api/projects/p1/deposit' && r.method === 'POST')
      .flush({ id: 'd1', projectId: 'p1', quoteNumber: 1042, quoteTotal: 6048.47, amount: 302.42, currency: 'USD', percent: 5, status: 'processing', paidAt: null, checkoutUrl: '/pay/d1' });
    await settle();
    expect(navigate).toHaveBeenCalledWith('/pay/d1');
  });

  it('a paid deposit shows the held price and the measure visit next', async () => {
    const el = await open(project({
      stage: 'measure', quote: { ...issued, status: 'accepted', held: true },
      deposit: { id: 'd1', projectId: 'p1', quoteNumber: 1042, quoteTotal: 6048.47, amount: 302.42, currency: 'USD', percent: 5, status: 'succeeded', paidAt: '2026-11-13T10:05:00Z', checkoutUrl: '/pay/d1' },
    }));
    expect(el.querySelector('[data-testid="held"]')?.textContent).toContain('$302.42');
    expect(el.querySelector('[data-testid="tracker"] [aria-current="step"]')?.textContent).toContain('Visit');

    // 6b: pick a measure-visit time.
    http.expectOne('/api/projects/p1/visit/slots').flush([
      { start: '2026-11-14T15:00:00Z', available: true },
      { start: '2026-11-14T19:00:00Z', available: false },
    ]);
    http.expectOne('/api/projects/p1/automated-calls').flush({ available: false, consented: false, wordingVersion: 'v7', wording: '', phone: null, hours: 'business', calls: [] });
    await settle();
    const slots = el.querySelectorAll<HTMLButtonElement>('[data-testid="visit-slot"]');
    expect([slots[0].disabled, slots[1].disabled]).toEqual([false, true]);
    slots[0].click();
    await settle();
    const book = http.expectOne((r) => r.url === '/api/projects/p1/visit' && r.method === 'POST');
    expect(book.request.body).toEqual({ start: '2026-11-14T15:00:00Z' });
    book.flush({ id: 'v1', start: '2026-11-14T15:00:00Z', status: 'booked', measuredAt: null });
    await settle();
    expect(el.querySelector('[data-testid="visit-booked"]')).not.toBeNull();
  });

  it('4b: the store offers automated calls; turning them on sends the wording version', async () => {
    const el = await open(project({
      stage: 'measure', quote: { ...issued, status: 'accepted', held: true },
      deposit: { id: 'd1', projectId: 'p1', quoteNumber: 1042, quoteTotal: 6048.47, amount: 302.42, currency: 'USD', percent: 5, status: 'succeeded', paidAt: '2026-11-13T10:05:00Z', checkoutUrl: '/pay/d1' },
    }));
    http.expectOne('/api/projects/p1/visit/slots').flush([]);
    http.expectOne('/api/projects/p1/automated-calls').flush({ available: true, consented: false, wordingVersion: 'v7', wording: 'Our automated assistant may call…', phone: null, hours: 'business', calls: [] });
    await settle();
    expect(el.querySelector('[data-testid="call-wording"]')?.textContent).toContain('automated assistant');
    el.querySelector<HTMLButtonElement>('[data-testid="calls-on"]')!.click();
    await settle();
    const put = http.expectOne((r) => r.url === '/api/projects/p1/automated-calls' && r.method === 'PUT');
    expect(put.request.body).toEqual({ consent: true, wordingVersion: 'v7' });
    put.flush({ available: true, consented: true, wordingVersion: 'v7', wording: '…', phone: '+1 555', hours: 'business',
      calls: [{ id: 'c1', status: 'scheduled', dueAt: '2026-11-13T15:00:00Z', attempts: 0, proposals: [], chosenStart: null, note: null }] });
    await settle();
    expect(el.querySelector('[data-testid="call-status"]')?.textContent).toContain('within your contact hours');
    expect(el.querySelector('[data-testid="calls-off"]')).not.toBeNull();
  });

  it('the installation shows its steps and date', async () => {
    const el = await open(project({
      stage: 'install', quote: { ...issued, number: 1043, status: 'accepted', held: true },
      install: { step: 'fabricating', scheduledFor: '2026-11-23T15:00:00Z', events: [
        { step: 'scheduled', at: '2026-11-15T10:00:00Z', scheduledFor: '2026-11-23T15:00:00Z', note: null },
        { step: 'fabricating', at: '2026-11-16T10:00:00Z', scheduledFor: null, note: null },
      ] },
    }));
    expect(el.querySelector('[data-testid="order-confirmed"]')?.textContent).toContain('scheduled for');
    const steps = [...el.querySelectorAll('[data-testid="install-steps"] li')];
    expect(steps.map((li) => li.className.includes('done'))).toEqual([true, false, false, false]);
    expect(steps[1].className).toContain('current');
    expect(el.querySelector('[data-testid="tracker"] [aria-current="step"]')?.textContent).toContain('Install');
  });

  it('a changed measured total waits for the customer to confirm it', async () => {
    const el = await open(project({
      stage: 'confirm', quote: { ...issued, number: 1043, total: 6500, held: false },
      deposit: { id: 'd1', projectId: 'p1', quoteNumber: 1042, quoteTotal: 6048.47, amount: 302.42, currency: 'USD', percent: 5, status: 'succeeded', paidAt: '2026-11-13T10:05:00Z', checkoutUrl: '/pay/d1' },
    }));
    expect(el.querySelector('[data-testid="confirm-measured"]')?.textContent).toContain('$6,500.00');
    expect(el.querySelector('[data-testid="accept-quote"]')).toBeNull();
    el.querySelector<HTMLButtonElement>('[data-testid="confirm-quote"]')!.click();
    await settle();
    http.expectOne((r) => r.url === '/api/projects/p1/confirm' && r.method === 'POST')
      .flush(project({ stage: 'install', quote: { ...issued, number: 1043, total: 6500, status: 'accepted', held: true } }));
    await settle();
    expect(el.querySelector('[data-testid="order-confirmed"]')).not.toBeNull();
  });

  it('an outdated quote offers a new one instead of Accept', async () => {
    const el = await open(project({ stage: 'quote', quote: { ...issued, current: false } }));
    expect(el.querySelector('[data-testid="outdated"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="accept-quote"]')).toBeNull();
  });

  it('a help-only store shows a Help chat; a guided-only store shows no chat', async () => {
    pkg.concierge.set(false);
    const help = await open(project());
    expect(help.querySelector('[data-testid="panel-title"]')?.textContent).toContain('Help');
  });

  it('a guided-only store has no chat column', async () => {
    pkg.concierge.set(false);
    pkg.helpChat.set(false);
    pkg.anyChat.set(false);
    const el = await open(project());
    expect(el.querySelector('bp-concierge-panel')).toBeNull();
  });
});
