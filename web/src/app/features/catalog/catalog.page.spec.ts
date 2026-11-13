import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CatalogPage } from './catalog.page';

const products = [
  { sku: 'PAINT-1G', name: 'Interior paint, white', description: null, unit: 'each', listPrice: 34.98, currency: 'USD', category: 'paint', imageUrl: '/products/paint-int-wht-1g.svg', attributes: {} },
  { sku: 'TILE-12', name: 'Ceramic floor tile', description: 'Per square metre', unit: 'm2', listPrice: 21.5, currency: 'USD', category: 'flooring', imageUrl: null, attributes: {} },
];

describe('CatalogPage', () => {
  let http: HttpTestingController;

  async function render() {
    const fixture = TestBed.createComponent(CatalogPage);
    TestBed.tick(); // httpResource sends its request from an effect
    return fixture;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [CatalogPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shows placeholders, then the tenant products with server prices', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="catalog-loading"]')).not.toBeNull();

    http.expectOne('/api/catalog/products').flush(products);
    await fixture.whenStable();

    const cards = el.querySelectorAll('[data-testid="product"]');
    expect(cards.length).toBe(2);
    expect(cards[0]!.textContent).toContain('Interior paint');
    expect(cards[0]!.querySelector('[data-testid="price"]')?.textContent).toContain('$34.98');
    expect(el.querySelector('[data-testid="count"]')?.textContent).toContain('2 of 2');
    // Image when the product has one (alt = product name), none otherwise.
    expect(cards[0]!.querySelector('img')?.getAttribute('alt')).toBe('Interior paint, white');
    expect(cards[1]!.querySelector('img')).toBeNull();
  });

  it('filters by name or SKU as you type', async () => {
    const fixture = await render();
    http.expectOne('/api/catalog/products').flush(products);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    const input = el.querySelector<HTMLInputElement>('[data-testid="search"]')!;
    input.value = 'tile';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();

    expect(el.querySelectorAll('[data-testid="product"]').length).toBe(1);
    expect(el.querySelector('[data-testid="count"]')?.textContent).toContain('1 of 2');

    input.value = 'nothing-like-this';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(el.querySelector('[data-testid="empty"]')?.textContent).toContain('Nothing matches');
  });

  it('explains a tenant refusal instead of showing products', async () => {
    const fixture = await render();
    http.expectOne('/api/catalog/products').flush({ code: 'multiple_active_memberships' }, { status: 409, statusText: 'Conflict' });
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="catalog-error"]')?.textContent).toContain('several stores');
    expect(el.querySelectorAll('[data-testid="product"]').length).toBe(0);
  });

  it('fails closed with a clear message when the tenant lookup is down', async () => {
    const fixture = await render();
    http.expectOne('/api/catalog/products').flush({ code: 'tenant_lookup_unavailable' }, { status: 503, statusText: 'Service Unavailable' });
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="catalog-error"]')?.textContent).toContain('could not confirm your tenant');
  });
});
