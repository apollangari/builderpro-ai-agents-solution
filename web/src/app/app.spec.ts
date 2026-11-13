import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideRouter } from '@angular/router';
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

async function render(oidc: ReturnType<typeof fakeOidc>) {
  await TestBed.configureTestingModule({
    imports: [App],
    providers: [provideRouter([]), { provide: OidcSecurityService, useValue: oidc }],
  }).compileComponents();
  const fixture = TestBed.createComponent(App);
  await fixture.whenStable();
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

  it('shows the retailer brand and project label from the theme', async () => {
    const el = await render(fakeOidc(false));

    expect(el.querySelector('[data-testid="retailer-name"]')?.textContent).toContain('BuilderPro Demo Store');
    expect(el.textContent).toContain('powered by BuilderPro');
    expect(el.querySelector('[data-testid="project-label"]')?.textContent).toContain('Sample project');
  });
});
