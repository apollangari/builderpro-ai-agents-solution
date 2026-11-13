import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { WhoAmIPage } from './whoami.page';
import { describeError } from './whoami.service';

const tokenView = {
  authenticated: true, userId: 'u-1', userName: 'alice', tenantId: 't-acme',
  roles: ['customer'], service: 'me-api', serverTimeUtc: '2026-11-12T20:00:00Z',
};
const platformView = {
  userKey: 'oidc_abc', userName: 'alice', email: null, roles: ['customer'],
  tenantId: 't-acme', customerId: 'c-alice', scopes: ['quotes.read'], source: 'membership',
};

describe('WhoAmIPage', () => {
  let http: HttpTestingController;

  async function render() {
    const fixture = TestBed.createComponent(WhoAmIPage);
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [WhoAmIPage],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('shows the token view and the platform view side by side', async () => {
    const fixture = await render();
    http.expectOne('/api/me').flush(tokenView);
    http.expectOne('/api/identity/me').flush(platformView);
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="tenant"]')?.textContent).toContain('t-acme');
    expect(el.querySelector('[data-testid="platform-tenant"]')?.textContent).toContain('t-acme');
  });

  it('keeps the token view when the platform refuses (no membership)', async () => {
    const fixture = await render();
    http.expectOne('/api/me').flush({ ...tokenView, tenantId: null });
    http.expectOne('/api/identity/me').flush({ code: 'no_membership' }, { status: 403, statusText: 'Forbidden' });
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="me"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="platform-error"]')?.textContent).toContain('not a member of any tenant');
  });
});

describe('describeError', () => {
  it.each([
    [{ status: 409, error: { code: 'multiple_active_memberships' } }, 'several tenants'],
    [{ status: 403, error: { code: 'tenant_not_permitted' } }, 'not a member of'],
    [{ status: 401, error: null }, '401'],
    [{ status: 429, error: null }, 'rate limit'],
    [{ status: 0, error: null }, 'Could not reach'],
  ])('explains %o', (err, expected) => {
    expect(describeError(err)).toContain(expected);
  });
});
