import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { SignupPage } from './signup.page';
import { WelcomePage } from './welcome.page';
import { PendingSignup } from './pending-signup';

function fakeOidc(authenticated: boolean) {
  return {
    authenticated: signal({ isAuthenticated: authenticated, allConfigsAuthenticated: [] }),
    authorize: vi.fn(),
  };
}

describe('SignupPage (store sign-up link)', () => {
  afterEach(() => sessionStorage.clear());

  async function render(store: string | undefined, authenticated = false) {
    const oidc = fakeOidc(authenticated);
    TestBed.configureTestingModule({
      imports: [SignupPage],
      providers: [provideRouter([]), { provide: OidcSecurityService, useValue: oidc }],
    });
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(SignupPage);
    fixture.componentRef.setInput('store', store);
    fixture.detectChanges();
    await fixture.whenStable();
    return { el: fixture.nativeElement as HTMLElement, oidc, navigate };
  }

  it('remembers the store and opens Create account directly', async () => {
    const { el, oidc } = await render('acme');

    expect(TestBed.inject(PendingSignup).get()).toBe('acme');
    expect(oidc.authorize).toHaveBeenCalledWith(undefined, { customParams: { prompt: 'create' } });
    expect(el.querySelector('[data-testid="signup-redirecting"]')).not.toBeNull();
  });

  it('already signed in: goes straight to /welcome to join the store', async () => {
    const { oidc, navigate } = await render('acme', true);

    expect(oidc.authorize).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/welcome']);
  });

  it.each([undefined, '', 'bad store!'])('rejects a link without a valid store (%s)', async (store) => {
    const { el, oidc } = await render(store);

    expect(oidc.authorize).not.toHaveBeenCalled();
    expect(TestBed.inject(PendingSignup).get()).toBeNull();
    expect(el.querySelector('[data-testid="signup-invalid"]')?.textContent).toContain('Ask your retailer');
  });
});

describe('WelcomePage (finish joining the store)', () => {
  let http: HttpTestingController;

  afterEach(() => {
    http.verify();
    sessionStorage.clear();
  });

  async function render(pendingStore: string | null) {
    if (pendingStore) sessionStorage.setItem('bp.signupStore', pendingStore);
    TestBed.configureTestingModule({
      imports: [WelcomePage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(WelcomePage);
    TestBed.tick(); // httpResource sends from an effect
    return fixture;
  }

  it('joins the pending store once, greets by store name and forgets the link', async () => {
    const fixture = await render('acme');

    const req = http.expectOne('/api/identity/onboarding');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ store: 'acme' });
    req.flush({ status: 'joined', storeName: 'Acme Home Improvement', tenantId: 't-acme', customerId: 'c-1' }, { status: 201, statusText: 'Created' });
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="welcome-store"]')?.textContent).toContain('Acme Home Improvement');
    expect(sessionStorage.getItem('bp.signupStore')).toBeNull();
  });

  it('explains a refused sign-up in a warning box', async () => {
    const fixture = await render('acme');

    http.expectOne('/api/identity/onboarding').flush({ code: 'membership_inactive' }, { status: 403, statusText: 'Forbidden' });
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="welcome-error"]')?.textContent).toContain('access to this store was removed');
    expect(sessionStorage.getItem('bp.signupStore')).toBeNull();
  });

  it('does nothing without a pending store', async () => {
    const fixture = await render(null);
    await fixture.whenStable();

    http.expectNone('/api/identity/onboarding');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="welcome-nothing"]')).not.toBeNull();
  });
});
