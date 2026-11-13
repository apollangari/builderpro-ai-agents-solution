import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { AccountPage, Profile } from './account.page';

const alice: Profile = {
  firstName: 'Alice', lastName: 'Acme', email: 'alice@example.test', phone: '+1 555 0100', addressLine1: '1 Main St', addressLine2: null,
  city: 'Prosper', region: 'TX', postalCode: '75078', country: 'US', contactConsent: 'both', contactHours: 'business', canChangePassword: true,
};

describe('AccountPage', () => {
  let http: HttpTestingController;
  let oidc: { forceRefreshSession: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    oidc = { forceRefreshSession: vi.fn(() => of({})) };
    TestBed.configureTestingModule({
      imports: [AccountPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: OidcSecurityService, useValue: oidc }],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  const settle = async () => {
    TestBed.tick();
    await new Promise((r) => setTimeout(r));
    TestBed.tick();
  };
  const type = async (el: HTMLElement, testId: string, value: string) => {
    const input = el.querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await settle();
  };

  async function open(profile: Profile) {
    const fixture = TestBed.createComponent(AccountPage);
    await settle();
    http.expectOne('/api/identity/profile').flush(profile);
    await settle();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the profile as editable fields and saves only after a change', async () => {
    const el = await open(alice);
    expect(el.querySelector<HTMLInputElement>('[data-testid="f-firstName"]')!.value).toBe('Alice');
    const save = el.querySelector<HTMLButtonElement>('[data-testid="save-profile"]')!;
    expect(save.disabled).toBe(true);

    await type(el, 'f-addressLine1', '9 Elm St');
    el.querySelector<HTMLInputElement>('[data-testid="consent-text"]')!.dispatchEvent(new Event('change'));
    await settle();
    expect(save.disabled).toBe(false);
    save.click();
    await settle();

    const put = http.expectOne((r) => r.url === '/api/identity/profile' && r.method === 'PUT');
    expect(put.request.body).toMatchObject({ addressLine1: '9 Elm St', contactConsent: 'text', firstName: 'Alice' });
    expect(put.request.body.email).toBeUndefined(); // the sign-in email isn't sent
    put.flush({ ...alice, addressLine1: '9 Elm St', contactConsent: 'text' });
    await settle();

    expect(el.querySelector('[data-testid="profile-saved"]')).not.toBeNull();
    expect(oidc.forceRefreshSession).toHaveBeenCalled(); // fresh tokens carry the new details
  });

  it("shows the server's field messages", async () => {
    const el = await open(alice);
    await type(el, 'f-phone', 'call me');
    el.querySelector<HTMLButtonElement>('[data-testid="save-profile"]')!.click();
    await settle();
    http.expectOne((r) => r.method === 'PUT').flush(
      { code: 'invalid_profile', errors: [{ field: 'phone', message: 'Enter a phone number like +1 555 123 4567.' }] },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();
    expect(el.textContent).toContain('Enter a phone number like');
  });

  it('changes the password in its own tab: confirm must match, then the server checks the current one', async () => {
    const el = await open(alice);
    el.querySelector<HTMLButtonElement>('[data-testid="tab-password"]')!.click();
    await settle();

    await type(el, 'pw-current', 'Passw0rd!');
    await type(el, 'pw-new', 'Better-Passw0rd');
    await type(el, 'pw-confirm', 'Better-Passw0rx');
    expect(el.querySelector('[data-testid="pw-mismatch"]')).not.toBeNull();
    expect(el.querySelector<HTMLButtonElement>('[data-testid="save-password"]')!.disabled).toBe(true);

    await type(el, 'pw-confirm', 'Better-Passw0rd');
    el.querySelector<HTMLButtonElement>('[data-testid="save-password"]')!.click();
    await settle();
    const post = http.expectOne((r) => r.url === '/api/identity/password' && r.method === 'POST');
    expect(post.request.body).toEqual({ currentPassword: 'Passw0rd!', newPassword: 'Better-Passw0rd' });
    post.flush(null, { status: 204, statusText: 'No Content' });
    await settle();
    expect(el.querySelector('[data-testid="pw-changed"]')).not.toBeNull();
  });

  it('a Google sign-in has no password tab', async () => {
    const el = await open({ ...alice, canChangePassword: false });
    expect(el.querySelector('[data-testid="tab-password"]')).toBeNull();
  });
});
