import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { NewQuotePage } from './new-quote.page';
import { StoreFeatures } from '../../core/tenant/store-features';
/** The store's package for the page (1h), without asking the server. */
function fakeFeatures(concierge = true, help = true) {
  return { concierge: signal(concierge), helpChat: signal(help), anyChat: signal(concierge || help), known: signal(true), features: signal([]) };
}


const CATEGORIES = [
  { id: 'countertops', name: 'Countertops', defaultUnit: 'sqft', scene: 'kitchen', attributes: [], typical: null },
  { id: 'roofing', name: 'Roofing', defaultUnit: 'bundle', scene: 'roof', attributes: [], typical: null },
  { id: 'labour', name: 'Installation & labour', defaultUnit: 'hour', scene: 'none', attributes: [], typical: null },
];
const product = (sku: string, category: string, listPrice: number) => ({
  sku, name: sku, description: null, unit: 'sqft', listPrice, currency: 'USD', category, imageUrl: `/products/${sku}.svg`, attributes: {},
});

describe('NewQuotePage (one step per screen)', () => {
  let http: HttpTestingController;

  function setup(postalCode: string | null) {
    TestBed.configureTestingModule({
      imports: [NewQuotePage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: OidcSecurityService, useValue: { userData: signal({ userData: postalCode ? { postal_code: postalCode } : {} }) } },
        { provide: StoreFeatures, useValue: fakeFeatures() },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(NewQuotePage);
    return fixture.nativeElement as HTMLElement;
  }

  afterEach(() => http.verify());

  const settle = async () => {
    TestBed.tick();
    await new Promise((r) => setTimeout(r));
    TestBed.tick();
  };
  const click = async (el: HTMLElement, testId: string) => {
    el.querySelector<HTMLElement>(`[data-testid="${testId}"]`)!.click();
    await settle();
  };
  async function categoriesLoaded(el: HTMLElement) {
    await settle();
    http.expectOne('/api/catalog/categories').flush(CATEGORIES);
    await settle();
    return el;
  }
  async function served(el: HTMLElement, zip: string, products: unknown[]) {
    http.expectOne(`/api/catalog/service-area?zip=${zip}`).flush({ zip, served: true });
    await settle();
    http.expectOne(`/api/catalog/products?zip=${zip}`).flush(products);
    await settle();
  }

  it('ZIP, then the types sold there, then product: one screen each, and choosing starts the project', async () => {
    const el = await categoriesLoaded(setup('62701'));

    // 1. Only the ZIP card, with one main button.
    expect(el.querySelector('[data-testid="step-category"]')).toBeNull();
    expect(el.querySelector('[data-testid="zip-yes"]')?.textContent).toContain('Use ZIP 62701');
    await click(el, 'zip-yes');
    await served(el, '62701', [product('CT-QTZ', 'countertops', 89), product('LABOR', 'labour', 65)]);

    // 2. Only types sold in that ZIP (no roofing here), with pictures; labour isn't a project type.
    expect(el.querySelector('[data-testid="step-zip"]')).toBeNull();
    expect(el.querySelector('[data-testid="type-countertops"] img')).not.toBeNull();
    expect(el.querySelector('[data-testid="type-roofing"]')).toBeNull();
    expect(el.querySelector('[data-testid="type-labour"]')).toBeNull();
    await click(el, 'type-countertops');

    // 3. Only that type's products, at the ZIP's price.
    const cards = el.querySelectorAll('[data-testid^="product-"]');
    expect(cards.length).toBe(1);
    expect(cards[0].querySelector('[data-testid="price"]')?.textContent).toContain('$89.00');

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await click(el, 'choose-CT-QTZ');
    const create = http.expectOne((r) => r.url === '/api/projects' && r.method === 'POST');
    expect(create.request.body).toEqual({ zip: '62701', title: 'Countertops · CT-QTZ' });
    create.flush({ id: 'p1' });
    await settle();
    const save = http.expectOne((r) => r.url === '/api/projects/p1/answers' && r.method === 'PUT');
    expect(save.request.body.sku).toBe('CT-QTZ');
    save.flush({ id: 'p1' });
    await settle();
    expect(navigate).toHaveBeenCalledWith(['/projects', 'p1']);
  });

  it('"Use a different ZIP" explains an unserved one; Back on products returns to the types', async () => {
    const el = await categoriesLoaded(setup('62701'));
    await click(el, 'zip-no');
    const input = el.querySelector<HTMLInputElement>('[data-testid="zip-input"]')!;
    input.value = '90210';
    input.dispatchEvent(new Event('input'));
    await settle();
    await click(el, 'zip-continue');
    http.expectOne('/api/catalog/service-area?zip=90210').flush({ zip: '90210', served: false });
    await settle();
    expect(el.querySelector('[data-testid="zip-not-served"]')?.textContent).toContain("We don't serve ZIP 90210");

    input.value = '60601';
    input.dispatchEvent(new Event('input'));
    await settle();
    await click(el, 'zip-continue');
    await served(el, '60601', [product('CT-QTZ', 'countertops', 99.68)]);
    await click(el, 'type-countertops');
    await click(el, 'back');
    expect(el.querySelector('[data-testid="step-category"]')).not.toBeNull();
  });

  it('without a ZIP on the account, asks for one straight away', async () => {
    const el = await categoriesLoaded(setup(null));
    expect(el.querySelector('[data-testid="zip-yes"]')).toBeNull();
    expect(el.querySelector('[data-testid="zip-input"]')).not.toBeNull();
  });

  it("the product screen's chat filters the catalog beside it; the chip clears it", async () => {
    const el = await categoriesLoaded(setup('75078'));
    await click(el, 'zip-yes');
    await served(el, '75078', [product('CT-QTZ', 'countertops', 89), product('CT-GRN', 'countertops', 72), product('CT-LAM', 'countertops', 28)]);
    await click(el, 'type-countertops');
    expect(el.querySelectorAll('[data-testid^="product-"]').length).toBe(3);
    expect(el.querySelector('[data-testid="zip-change"], .zip-pill')?.textContent).toContain('75078');

    const input = el.querySelector<HTMLInputElement>('[data-testid="catalog-input"]')!;
    input.value = 'white quartz under $90';
    input.dispatchEvent(new Event('input'));
    await settle();
    await click(el, 'catalog-send');
    const ask = http.expectOne('/api/assistant/concierge');
    expect(ask.request.body.catalog).toEqual({ zip: '75078', category: 'countertops' });
    ask.flush({ reply: 'One match: CT-QTZ at $89.00.', guarded: false, toolsUsed: ['filter_catalog'],
      filter: { attributes: { material: 'quartz', color: 'white' }, minPrice: null, maxPrice: 90, words: null, skus: ['CT-QTZ'] } });
    await settle();

    expect([...el.querySelectorAll('[data-testid^="product-"]')].map((a) => a.getAttribute('data-testid'))).toEqual(['product-CT-QTZ']);
    expect(el.querySelector('[data-testid="chat-filter"]')?.textContent).toContain('quartz · white · under $90');
    await click(el, 'clear-chat-filter');
    expect(el.querySelectorAll('[data-testid^="product-"]').length).toBe(3);
  });
});
