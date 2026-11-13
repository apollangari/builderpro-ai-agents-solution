import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { NewQuotePage } from './new-quote.page';

const CATEGORIES = [
  { id: 'countertops', name: 'Countertops', defaultUnit: 'sqft', scene: 'kitchen', attributes: [], typical: null },
  { id: 'roofing', name: 'Roofing', defaultUnit: 'bundle', scene: 'roof', attributes: [], typical: null },
  { id: 'sheds', name: 'Sheds', defaultUnit: 'each', scene: 'yard', attributes: [], typical: null },
  { id: 'labour', name: 'Installation & labour', defaultUnit: 'hour', scene: 'none', attributes: [], typical: null },
];
const product = (sku: string, category: string, listPrice: number) => ({
  sku, name: sku, description: null, unit: 'sqft', listPrice, currency: 'USD', category, imageUrl: null, attributes: {},
});

describe('NewQuotePage (2n-a)', () => {
  let http: HttpTestingController;

  function setup(postalCode: string | null) {
    TestBed.configureTestingModule({
      imports: [NewQuotePage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: OidcSecurityService, useValue: { userData: signal({ userData: postalCode ? { postal_code: postalCode } : {} }) } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(NewQuotePage);
    return { fixture, el: fixture.nativeElement as HTMLElement };
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

  it('confirms the sign-up ZIP, then shows only the types and prices sold there', async () => {
    const { el } = setup('62701');
    await settle();
    http.expectOne('/api/catalog/categories').flush(CATEGORIES);
    await settle();

    expect(el.querySelector('[data-testid="step-zip"]')?.textContent).toContain('Is this project at ZIP 62701?');
    await click(el, 'zip-yes');

    http.expectOne('/api/catalog/service-area?zip=62701').flush({ zip: '62701', served: true });
    await settle();
    http.expectOne('/api/catalog/products?zip=62701').flush([product('CT-QTZ', 'countertops', 89), product('ROOF-ARCH', 'roofing', 42.97), product('LABOR', 'labour', 65)]);
    await settle();

    expect(el.querySelector('[data-testid="zip-chosen"]')?.textContent).toContain('62701');
    expect(el.querySelector('[data-testid="type-countertops"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="type-sheds"]')).toBeNull(); // not sold in this ZIP
    expect(el.querySelector('[data-testid="type-labour"]')).toBeNull(); // not a project type on its own

    await click(el, 'type-countertops');
    const cards = el.querySelectorAll('[data-testid^="product-"]');
    expect(cards.length).toBe(1);
    expect(cards[0].querySelector('[data-testid="price"]')?.textContent).toContain('$89.00');
  });

  it('"No" asks for the project ZIP and explains an unserved one', async () => {
    const { el } = setup('62701');
    await settle();
    http.expectOne('/api/catalog/categories').flush(CATEGORIES);
    await settle();

    await click(el, 'zip-no');
    const input = el.querySelector<HTMLInputElement>('[data-testid="zip-input"]')!;
    input.value = '90210';
    input.dispatchEvent(new Event('input'));
    await settle();
    await click(el, 'zip-continue');

    http.expectOne('/api/catalog/service-area?zip=90210').flush({ zip: '90210', served: false });
    await settle();

    expect(el.querySelector('[data-testid="zip-not-served"]')?.textContent).toContain("We don't serve ZIP 90210");
    expect(el.querySelector('[data-testid="step-category"]')).toBeNull();
  });

  it('without a ZIP on the account, asks for one straight away', async () => {
    const { el } = setup(null);
    await settle();
    http.expectOne('/api/catalog/categories').flush(CATEGORIES);
    await settle();

    expect(el.querySelector('[data-testid="zip-yes"]')).toBeNull();
    expect(el.querySelector('[data-testid="zip-input"]')).not.toBeNull();
  });
});
