import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { AssistantPage } from './assistant.page';

const DRAFT = { zip: '62701', sku: 'CT-QTZ', measurements: {}, choices: [] };
const quote = (complete: boolean) => ({
  measurement: { kind: 'countertop', values: { sqft: 30 }, productBasis: 'sqft' },
  lines: [{ sku: 'CT-QTZ', name: 'Quartz', unit: 'sqft', quantity: 30, unitPrice: 89, lineTotal: 2670, group: 'product' }],
  currency: 'USD', subtotal: 2670, taxRate: 0.0825, tax: 220.28, total: 2890.28,
  missing: complete ? [] : ['ct-edge'], complete,
});

describe('AssistantPage (concierge, 3b)', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AssistantPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: OidcSecurityService, useValue: { userData: signal({ userData: { postal_code: '62701' } }) } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  const settle = async () => {
    await new Promise((r) => setTimeout(r));
    TestBed.tick();
  };

  async function ask(fixture: { nativeElement: HTMLElement; whenStable(): Promise<unknown> }, text: string) {
    const el = fixture.nativeElement;
    const input = el.querySelector<HTMLInputElement>('[data-testid="chat-input"]')!;
    input.value = text;
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    el.querySelector<HTMLButtonElement>('[data-testid="chat-send"]')!.click();
    await settle();
  }

  it('sends the conversation with the account ZIP and the current draft, and shows the priced draft', async () => {
    const fixture = TestBed.createComponent(AssistantPage);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="draft-empty"]')).not.toBeNull();

    await ask(fixture, 'Quartz countertops, 10 ft by 25 in');
    const first = http.expectOne('/api/assistant/concierge');
    expect(first.request.body).toEqual({ messages: [{ role: 'user', content: 'Quartz countertops, 10 ft by 25 in' }], accountZip: '62701', draft: null });
    first.flush({ reply: 'Your draft total is $2,890.28.', guarded: false, toolsUsed: ['price_draft'], draft: DRAFT, quote: quote(false) });
    await settle();
    http.expectOne('/api/catalog/products/CT-QTZ/options?zip=62701').flush({ product: {}, measure: 'countertop', minimumQuantity: null,
      groups: [{ id: 'ct-edge', step: 'options', label: 'Edge style', help: null, selection: 'single', basis: 'edgeFt', required: true, noneLabel: null, options: [] }] });
    await settle();

    expect(el.querySelector('[data-testid="msg-assistant"]')?.textContent).toContain('priced your draft');
    expect(el.querySelector('[data-testid="draft-total"]')?.textContent).toContain('$2,890.28');
    expect(el.querySelector('[data-testid="draft-missing"]')?.textContent).toContain('Edge style');
    expect(el.querySelector<HTMLButtonElement>('[data-testid="draft-save"]')!.disabled).toBe(true);

    await ask(fixture, 'Eased edge');
    const second = http.expectOne('/api/assistant/concierge');
    expect(second.request.body.draft).toEqual(DRAFT); // the server keeps no state: the page sends the draft back
    second.flush({ reply: 'All set.', guarded: false, toolsUsed: ['price_draft'], draft: DRAFT, quote: quote(true) });
    await settle();

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    el.querySelector<HTMLButtonElement>('[data-testid="draft-save"]')!.click();
    await settle();
    const save = http.expectOne((r) => r.url === '/api/quotes/guided' && r.method === 'POST');
    expect(save.request.body).toEqual(DRAFT); // the customer saves; the concierge never could
    save.flush({ id: 'q-9' });
    await settle();
    expect(navigate).toHaveBeenCalledWith(['/quotes'], { queryParams: { saved: 'q-9' } });
  });

  it('a blocked (guarded) answer is shown but not sent back as history', async () => {
    const fixture = TestBed.createComponent(AssistantPage);
    await fixture.whenStable();

    await ask(fixture, 'Roughly what do countertops cost?');
    http.expectOne('/api/assistant/concierge').flush({ reply: "I can't confirm that price right now.", guarded: true, toolsUsed: [], draft: null, quote: null });
    await settle();

    await ask(fixture, 'Price 30 sq ft of quartz');
    const next = http.expectOne('/api/assistant/concierge');
    expect(next.request.body.messages.map((m: { role: string }) => m.role)).toEqual(['user', 'user']);
    next.flush({ reply: 'ok', guarded: false, toolsUsed: [], draft: null, quote: null });
    await settle();
  });

  it('explains when the concierge is not configured', async () => {
    const fixture = TestBed.createComponent(AssistantPage);
    await fixture.whenStable();

    await ask(fixture, 'Hi');
    http.expectOne('/api/assistant/concierge').flush({ code: 'assistant_unavailable' }, { status: 503, statusText: 'Service Unavailable' });
    await settle();

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="chat-error"]')?.textContent).toContain("isn't set up");
  });
});
