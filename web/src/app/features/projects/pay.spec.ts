import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { PayPage } from './pay.page';

describe('PayPage (6a, test mode)', () => {
  it('shows the server amount and pays, then returns to the project', async () => {
    TestBed.configureTestingModule({ imports: [PayPage], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const http = TestBed.inject(HttpTestingController);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const settle = async () => {
      TestBed.tick();
      await new Promise((r) => setTimeout(r));
      TestBed.tick();
    };
    const fixture = TestBed.createComponent(PayPage);
    fixture.componentRef.setInput('id', 'd1');
    await settle();
    const deposit = { id: 'd1', projectId: 'p1', quoteNumber: 1004, quoteTotal: 2829.66, amount: 141.48, currency: 'USD', percent: 5, status: 'pending', paidAt: null, checkoutUrl: '/pay/d1' };
    http.expectOne('/api/deposits/d1').flush(deposit);
    await settle();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="pay-amount"]')?.textContent).toContain('$141.48');
    expect(el.querySelector('[data-testid="test-mode"]')).not.toBeNull();
    el.querySelector<HTMLButtonElement>('[data-testid="pay-now"]')!.click();
    await settle();
    http.expectOne((r) => r.url === '/api/deposits/d1/pay' && r.method === 'POST').flush({ ...deposit, status: 'paid', paidAt: '2026-11-13T10:00:00Z' });
    await settle();
    expect(navigate).toHaveBeenCalledWith(['/projects', 'p1']);
    http.verify();
  });
});
