import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CatalogPage } from './catalog.page';

const products = [
  { sku: 'PAINT-1G', name: 'Interior paint, white', description: null, unit: 'each', listPrice: 34.98, currency: 'USD', category: 'paint', imageUrl: '/products/paint-int-wht-1g.svg', attributes: { finish: 'eggshell', color: 'White' } },
  { sku: 'TILE-12', name: 'Ceramic floor tile', description: 'Per square foot', unit: 'sqft', listPrice: 2, currency: 'USD', category: 'flooring', imageUrl: null, attributes: { material: 'tile', color: 'Beige' } },
  { sku: 'LVP-OAK', name: 'Vinyl plank oak', description: null, unit: 'sqft', listPrice: 3.49, currency: 'USD', category: 'flooring', imageUrl: '/products/flr-lvp-oak.svg', attributes: { material: 'vinyl-plank', color: 'Oak' } },
];

const categories = [
  { id: 'flooring', name: 'Flooring', defaultUnit: 'sqft', scene: 'room-floor', typical: null, attributes: [
    { key: 'material', label: 'Material', kind: 'choice', required: true, options: ['vinyl-plank', 'laminate', 'tile'], unit: null },
    { key: 'color', label: 'Colour', kind: 'text', required: true, options: null, unit: null },
  ] },
  { id: 'paint', name: 'Paint', defaultUnit: 'each', scene: 'room-wall', typical: null, attributes: [
    { key: 'finish', label: 'Finish', kind: 'choice', required: true, options: ['flat', 'eggshell'], unit: null },
    { key: 'color', label: 'Colour', kind: 'text', required: true, options: null, unit: null },
  ] },
];

type Fixture = { whenStable(): Promise<unknown> };

describe('CatalogPage (product picker)', () => {
  let http: HttpTestingController;

  /** Renders the page; answers the categories request, and the products request unless told not to. */
  async function render(answerProducts: object | null = products) {
    const fixture = TestBed.createComponent(CatalogPage);
    TestBed.tick(); // httpResource sends from an effect
    http.expectOne('/api/catalog/categories').flush(categories);
    if (answerProducts !== null) {
      http.expectOne('/api/catalog/products').flush(answerProducts);
      await fixture.whenStable();
    }
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  const skus = (el: HTMLElement) => [...el.querySelectorAll('[data-testid="product"] .badge.mono')].map((b) => b.textContent?.trim());

  async function click(fixture: Fixture, el: HTMLElement, testId: string) {
    el.querySelector<HTMLElement>(`[data-testid="${testId}"]`)!.click();
    await fixture.whenStable();
  }

  async function choose(fixture: Fixture, el: HTMLElement, testId: string, value: string) {
    const select = el.querySelector<HTMLSelectElement>(`[data-testid="${testId}"]`)!;
    select.value = value;
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [CatalogPage], providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shows placeholders, then every product with image, badges and server price', async () => {
    const fixture = TestBed.createComponent(CatalogPage);
    TestBed.tick();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="catalog-loading"]')).not.toBeNull();

    http.expectOne('/api/catalog/categories').flush(categories);
    http.expectOne('/api/catalog/products').flush(products);
    await fixture.whenStable();

    const cards = el.querySelectorAll('[data-testid="product"]');
    expect(cards.length).toBe(3);
    expect(cards[0]!.querySelector('[data-testid="price"]')?.textContent).toContain('$34.98');
    expect(cards[0]!.querySelector('img')?.getAttribute('alt')).toBe('Interior paint, white');
    expect(cards[1]!.querySelector('img')).toBeNull();
    expect(cards[0]!.querySelector('[data-testid="attributes"]')?.textContent).toContain('eggshell');
    expect(el.querySelector('[data-testid="count"]')?.textContent).toContain('3 of 3');
  });

  it('offers a tab per category the store sells, with counts', async () => {
    const { el } = await render();

    const tabs = [...el.querySelectorAll('[role="tab"]')].map((t) => t.textContent?.replace(/\s+/g, ' ').trim());
    expect(tabs).toEqual(['All 3', 'Flooring 2', 'Paint 1']);
    expect(el.querySelector('[data-testid="tab-all"]')?.getAttribute('aria-selected')).toBe('true');
  });

  it("a tab shows its products and that category's attribute filters", async () => {
    const { fixture, el } = await render();

    await click(fixture, el, 'tab-flooring');
    expect(skus(el)).toEqual(['TILE-12', 'LVP-OAK']);
    expect(el.querySelector('[data-testid="filter-material"]')).not.toBeNull();

    await choose(fixture, el, 'filter-material', 'vinyl-plank');
    expect(skus(el)).toEqual(['LVP-OAK']);
    expect(el.querySelector('[data-testid="count"]')?.textContent).toContain('1 of 3');
  });

  it('switching tabs clears filters; Clear filters resets them', async () => {
    const { fixture, el } = await render();
    await click(fixture, el, 'tab-flooring');
    await choose(fixture, el, 'filter-material', 'tile');
    expect(skus(el)).toEqual(['TILE-12']);

    await click(fixture, el, 'clear-filters');
    expect(skus(el)).toEqual(['TILE-12', 'LVP-OAK']);

    await choose(fixture, el, 'filter-material', 'tile');
    await click(fixture, el, 'tab-all');
    expect(skus(el)).toEqual(['PAINT-1G', 'TILE-12', 'LVP-OAK']);
    expect(el.querySelector('[data-testid="filters"]')).toBeNull(); // no filters on "All"
  });

  it('search combines with the tab, and an empty result says why', async () => {
    const { fixture, el } = await render();
    await click(fixture, el, 'tab-flooring');

    const input = el.querySelector<HTMLInputElement>('[data-testid="search"]')!;
    input.value = 'oak';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(skus(el)).toEqual(['LVP-OAK']);

    input.value = 'nothing-like-this';
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(el.querySelector('[data-testid="empty"]')?.textContent).toContain('Nothing matches');
  });

  it('explains a tenant refusal instead of showing products', async () => {
    const { fixture, el } = await render(null);
    http.expectOne('/api/catalog/products').flush({ code: 'multiple_active_memberships' }, { status: 409, statusText: 'Conflict' });
    await fixture.whenStable();

    expect(el.querySelector('[data-testid="catalog-error"]')?.textContent).toContain('several stores');
    expect(el.querySelectorAll('[data-testid="product"]').length).toBe(0);
  });

  it('fails closed with a clear message when the tenant lookup is down', async () => {
    const { fixture, el } = await render(null);
    http.expectOne('/api/catalog/products').flush({ code: 'tenant_lookup_unavailable' }, { status: 503, statusText: 'Service Unavailable' });
    await fixture.whenStable();

    expect(el.querySelector('[data-testid="catalog-error"]')?.textContent).toContain('could not confirm your tenant');
  });
});
