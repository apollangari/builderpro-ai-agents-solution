import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { StoreFeatures } from '../../core/tenant/store-features';
import { SalesPage } from './sales.page';

describe('SalesPage (1i)', () => {
  let http: HttpTestingController;
  const report = {
    from: '2026-11-01T00:00:00Z', to: '2026-12-01T00:00:00Z',
    salespeople: [{ salespersonKey: 'k', name: 'Sam Sales', sales: 1, soldTotal: 2829.66, currency: 'USD', commissionPercent: 3, commission: 84.89, lines: [] }],
  };

  function setup(salesperson = true) {
    TestBed.configureTestingModule({
      imports: [SalesPage],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: StoreFeatures, useValue: { salesperson: signal(salesperson), salesDesk: signal(true), isCustomer: signal(false) } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(SalesPage);
    TestBed.tick();
    http.expectOne('/api/sales/commission').flush(report);
    return fixture;
  }
  afterEach(() => http.verify());

  it('finds a customer and starts the quote steps for them', async () => {
    const fixture = setup();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await fixture.whenStable();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="commission-row"]')?.textContent).toContain('Sam Sales');
    expect(el.querySelector('[data-testid="commission-row"]')?.textContent).toContain('$84.89');

    const input = el.querySelector<HTMLInputElement>('[data-testid="customer-query"]')!;
    input.value = 'alice';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    el.querySelector<HTMLButtonElement>('[data-testid="customer-find"]')!.click();
    TestBed.tick();
    http.expectOne('/api/identity/customers?q=alice').flush([
      { customerId: 'c-alice', name: 'Alice Acme', email: 'alice@acme.test', phone: null, postalCode: '75078' },
    ]);
    await fixture.whenStable();

    el.querySelector<HTMLButtonElement>('[data-testid="start-for"]')!.click();
    expect(navigate).toHaveBeenCalledWith(['/quote/new'], { queryParams: { for: 'c-alice', name: 'Alice Acme', zip: '75078' } });
  });

  it('a manager sees the report without the customer search', async () => {
    const fixture = setup(false);
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('[data-testid="customer-search"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="commission"]')).not.toBeNull();
  });
});
