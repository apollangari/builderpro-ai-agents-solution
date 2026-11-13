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
async function render(oidc: ReturnType<typeof fakeOidc>, answerConsentSync = true) {
  await TestBed.configureTestingModule({
    imports: [App],
    providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: OidcSecurityService, useValue: oidc }],
  }).compileComponents();
  const fixture = TestBed.createComponent(App);
  TestBed.tick();
  if (answerConsentSync) {
    TestBed.inject(HttpTestingController).match('/api/identity/consent/sync').forEach((r) => r.flush({ recorded: false, current: null }));
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
    expect(el.querySelector('.avatar')?.textContent?.trim()).toBe('AA');
  });

  it('syncs contact consent once when signed in', async () => {
    await render(fakeOidc(true, { name: 'Alice Acme' }), false);
    const http = TestBed.inject(HttpTestingController);
    const req = http.expectOne('/api/identity/consent/sync');
    expect(req.request.method).toBe('POST');
    req.flush({ recorded: false, current: null });
    http.verify();
  });

  it('does not sync consent when signed out', async () => {
    await render(fakeOidc(false));
    TestBed.inject(HttpTestingController).expectNone('/api/identity/consent/sync');
  });

  it('shows the retailer brand and project label from the theme', async () => {
    const el = await render(fakeOidc(false));

    expect(el.querySelector('[data-testid="retailer-name"]')?.textContent).toContain('BuilderPro Demo Store');
    expect(el.textContent).toContain('powered by BuilderPro');
    expect(el.querySelector('[data-testid="project-label"]')?.textContent).toContain('Sample project');
  });
});
