import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { AccountPage } from './account.page';

describe('AccountPage', () => {
  function render(claims: Record<string, string>) {
    const oidc = { userData: signal({ userData: claims }), authorize: vi.fn() };
    TestBed.configureTestingModule({ imports: [AccountPage], providers: [provideRouter([]), { provide: OidcSecurityService, useValue: oidc }] });
    const fixture = TestBed.createComponent(AccountPage);
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, oidc };
  }

  const alice = {
    name: 'Alice Acme', email: 'alice@example.test', phone: '+1 555 0100', address_line1: '1 Main St', city: 'Prosper', region: 'TX',
    postal_code: '75078', contact_consent: 'both', contact_hours: 'business',
  };

  it('shows the profile and offers edit profile and change password for an email account', () => {
    const { el, oidc } = render(alice);
    expect(el.querySelector('[data-testid="profile-name"]')?.textContent).toContain('Alice Acme');
    expect(el.querySelector('[data-testid="profile-address"]')?.textContent).toContain('1 Main St · Prosper, TX · 75078');
    expect(el.querySelector('[data-testid="profile-consent"]')?.textContent).toContain('Calls and texts');
    expect(el.querySelector('[data-testid="profile-method"]')?.textContent).toContain('Email and password');

    el.querySelector<HTMLButtonElement>('[data-testid="edit-profile"]')!.click();
    expect(oidc.authorize).toHaveBeenCalledWith(undefined, { customParams: { kc_action: 'UPDATE_PROFILE' } });
    el.querySelector<HTMLButtonElement>('[data-testid="change-password"]')!.click();
    expect(oidc.authorize).toHaveBeenCalledWith(undefined, { customParams: { kc_action: 'UPDATE_PASSWORD' } });
  });

  it('a Google sign-in can edit the profile but has no password to change here', () => {
    const { el } = render({ ...alice, identity_provider: 'google' });
    expect(el.querySelector('[data-testid="change-password"]')).toBeNull();
    expect(el.querySelector('[data-testid="google-note"]')?.textContent).toContain('Google');
    expect(el.querySelector('[data-testid="edit-profile"]')).not.toBeNull();
  });
});
