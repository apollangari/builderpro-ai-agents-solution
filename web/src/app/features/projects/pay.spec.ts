import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { PayPage, leaveFor } from './pay.page';

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
    const deposit = { id: 'd1', projectId: 'p1', quoteNumber: 1004, quoteTotal: 2829.66, amount: 141.48, currency: 'USD', percent: 5, status: 'processing', paidAt: null, checkoutUrl: '/pay/d1' };
    http.expectOne('/api/deposits/d1').flush(deposit);
    await settle();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="pay-amount"]')?.textContent).toContain('$141.48');
    expect(el.querySelector('[data-testid="test-mode"]')).not.toBeNull();
    el.querySelector<HTMLButtonElement>('[data-testid="pay-now"]')!.click();
    await settle();
    http.expectOne((r) => r.url === '/api/deposits/d1/pay' && r.method === 'POST').flush({ ...deposit, status: 'succeeded', paidAt: '2026-11-13T10:00:00Z' });
    await settle();
    expect(navigate).toHaveBeenCalledWith(['/projects', 'p1']);
    http.verify();
  });

  const base = { id: 'd1', projectId: 'p1', quoteNumber: 1004, quoteTotal: 2829.66, amount: 141.48, currency: 'USD', percent: 5, paidAt: null };
  async function open(inputs: Record<string, string>) {
    TestBed.configureTestingModule({ imports: [PayPage], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const http = TestBed.inject(HttpTestingController);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(PayPage);
    for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
    const settle = async () => {
      TestBed.tick();
      await new Promise((r) => setTimeout(r));
      TestBed.tick();
    };
    await settle();
    return { http, navigate, settle, el: fixture.nativeElement as HTMLElement };
  }

  it('Decline tests a failed payment (6d state machine)', async () => {
    const { http, settle, el } = await open({ id: 'd1' });
    http.expectOne('/api/deposits/d1').flush({ ...base, status: 'processing', checkoutUrl: '/pay/d1', provider: 'simulated' });
    await settle();
    el.querySelector<HTMLButtonElement>('[data-testid="pay-decline"]')!.click();
    await settle();
    const post = http.expectOne((r) => r.url === '/api/deposits/d1/pay');
    expect(post.request.body).toEqual({ outcome: 'decline' });
    post.flush({ ...base, status: 'failed', checkoutUrl: '/pay/d1', provider: 'simulated' });
    await settle();
    expect(el.querySelector('[data-testid="pay-failed"]')).not.toBeNull();
    http.verify();
  });

  it('a Stripe deposit continues on Stripe; coming back it confirms with the server, then opens the project', async () => {
    const go = vi.spyOn(leaveFor, 'go').mockImplementation(() => undefined);
    const first = await open({ id: 'd1' });
    first.http.expectOne('/api/deposits/d1').flush({ ...base, status: 'processing', checkoutUrl: 'https://checkout.stripe.test/cs_1', provider: 'stripe' });
    await first.settle();
    expect(first.el.querySelector('[data-testid="pay-now"]')).toBeNull();   // the browser can't pay a Stripe deposit itself
    first.el.querySelector<HTMLButtonElement>('[data-testid="pay-stripe"]')!.click();
    expect(go).toHaveBeenCalledWith('https://checkout.stripe.test/cs_1');
    first.http.verify();
    TestBed.resetTestingModule();

    const back = await open({ id: 'd1', return: 'stripe' });
    back.http.expectOne('/api/deposits/d1?refresh=true').flush({ ...base, status: 'succeeded', paidAt: '2026-11-13T10:00:00Z', checkoutUrl: 'https://checkout.stripe.test/cs_1', provider: 'stripe' });
    await back.settle();
    expect(back.navigate).toHaveBeenCalledWith(['/projects', 'p1']);
    back.http.verify();
  });

  it('coming back before the answer shows "confirming"', async () => {
    const { http, settle, el } = await open({ id: 'd1', return: 'stripe' });
    http.expectOne('/api/deposits/d1?refresh=true').flush({ ...base, status: 'processing', checkoutUrl: 'https://checkout.stripe.test/cs_1', provider: 'stripe' });
    await settle();
    expect(el.querySelector('[data-testid="pay-checking"]')?.textContent).toContain('Confirming');
    http.verify();
  });
});
