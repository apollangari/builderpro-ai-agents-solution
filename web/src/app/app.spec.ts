import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { of } from 'rxjs';
import { App } from './app';

/** A tiny stand-in for the OIDC service: just the signals and methods the shell uses. */
function fakeOidc(authenticated: boolean, user: Record<string, unknown> | null = null) {
  return {
    authenticated: signal({ isAuthenticated: authenticated, allConfigsAuthenticated: [] }),
    userData: signal({ userData: user, allUserData: [] }),
    authorize: vi.fn(),
    logoff: vi.fn(() => of(null)),
  };
}

/**
 * Renders the shell. A signed-in shell POSTs a consent sync (httpResource); a pending request keeps
 * the app "unstable", so by default it is answered here. Pass answerConsentSync=false to inspect it.
 */
async function render(oidc: ReturnType<typeof fakeOidc>, answerConsentSync = true, stores: unknown[] = []) {
  await TestBed.configureTestingModule({
    imports: [App],
    providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: OidcSecurityService, useValue: oidc }],
  }).compileComponents();
  const fixture = TestBed.createComponent(App);
  TestBed.tick();
  if (answerConsentSync) {
    const http = TestBed.inject(HttpTestingController);
    http.match('/api/identity/consent/sync').forEach((r) => r.flush({ recorded: false, current: null }));
    http.match('/api/identity/memberships/mine').forEach((r) => r.flush(stores));
    await fixture.whenStable();
  }
  return fixture.nativeElement as HTMLElement;
}

describe('App shell', () => {
  it('offers sign-in when signed out, and starts the OIDC redirect on click', async () => {
    const oidc = fakeOidc(false);
    const el = await render(oidc);

    const button = el.querySelector<HTMLButtonElement>('[data-testid="sign-in"]');
    expect(button).not.toBeNull();
    button!.click();
    expect(oidc.authorize).toHaveBeenCalledOnce();
  });

  it('shows the user name and sign-out when signed in', async () => {
    const el = await render(fakeOidc(true, { name: 'Alice Acme' }));

    expect(el.querySelector('[data-testid="user-name"]')?.textContent).toContain('Alice Acme');
    expect(el.querySelector('[data-testid="sign-in"]')).toBeNull();
    expect(el.querySelector('.avatar [aria-hidden="true"]')?.textContent?.trim()).toBe('AA');
  });

  it('the avatar opens the account menu: My projects, My quotes, account details, sign out', async () => {
    const el = await render(fakeOidc(true, { name: 'Alice Acme' }));
    expect(el.querySelector('[data-testid="account-menu"]')).toBeNull();

    const button = el.querySelector<HTMLButtonElement>('[data-testid="account-button"]')!;
    expect(button.getAttribute('aria-expanded')).toBe('false');
    button.click();
    TestBed.tick();

    const menu = el.querySelector('[data-testid="account-menu"]')!;
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect([...menu.querySelectorAll('[role="menuitem"]')].map((m) => m.textContent?.trim())).toEqual(['My projects', 'My quotes', 'Account', 'Sign out']);
    expect(el.querySelector('nav')).toBeNull(); // no top menu: the customer is in one flow (owner)
  });

  it('syncs contact consent once when signed in', async () => {
    await render(fakeOidc(true, { name: 'Alice Acme' }), false);
    const http = TestBed.inject(HttpTestingController);
    const req = http.expectOne('/api/identity/consent/sync');
    expect(req.request.method).toBe('POST');
    req.flush({ recorded: false, current: null });
    http.expectOne('/api/identity/memberships/mine').flush([]);   // the store picker's request
    http.verify();
  });

  it('does not sync consent when signed out', async () => {
    await render(fakeOidc(false));
    TestBed.inject(HttpTestingController).expectNone('/api/identity/consent/sync');
  });

  it('shows a store picker only for customers of several stores', async () => {
    const one = await render(fakeOidc(true, { name: 'Alice Acme' }), true, [{ tenantId: 't-acme', storeName: 'Acme', customerId: 'c-1' }]);
    expect(one.querySelector('[data-testid="store-picker"]')).toBeNull();
  });

  it('lists every store in the picker', async () => {
    const el = await render(fakeOidc(true, { name: 'Dana' }), true, [
      { tenantId: 't-acme', storeName: 'Acme Home Improvement', customerId: 'c-a' },
      { tenantId: 't-globex', storeName: 'Globex Building Supply', customerId: 'c-g' },
    ]);
    const options = [...el.querySelectorAll('[data-testid="store-picker"] option')].map((o) => o.textContent?.trim());
    expect(options).toEqual(['Choose a store…', 'Acme Home Improvement', 'Globex Building Supply']);
  });

  it('keeps a remembered pick that is one of your stores, and drops one that is not', async () => {
    const stores = [
      { tenantId: 't-acme', storeName: 'Acme', customerId: 'c-a' },
      { tenantId: 't-globex', storeName: 'Globex', customerId: 'c-g' },
    ];
    sessionStorage.setItem('bp.selectedTenant', 't-acme');
    await render(fakeOidc(true, { name: 'Dana' }), true, stores);
    expect(sessionStorage.getItem('bp.selectedTenant')).toBe('t-acme');

    TestBed.resetTestingModule();
    sessionStorage.setItem('bp.selectedTenant', 't-initech');
    await render(fakeOidc(true, { name: 'Dana' }), true, stores);
    expect(sessionStorage.getItem('bp.selectedTenant')).toBeNull();
    sessionStorage.clear();
  });

  it('shows the retailer brand and project label from the theme', async () => {
    const el = await render(fakeOidc(false));

    expect(el.querySelector('[data-testid="retailer-name"]')?.textContent).toContain('BuilderPro Demo Store');
    expect(el.textContent).toContain('powered by BuilderPro');
    expect(el.querySelector('[data-testid="project-label"]')?.textContent).toContain('Sample project');
  });
});
