import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { WhoAmIPage } from './whoami.page';
import { describeError } from '../../core/api/describe-error';

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

  // httpResource sends its request from an effect: TestBed.tick() runs effects + change
  // detection, so the request is visible to HttpTestingController afterwards.
  async function render() {
    const fixture = TestBed.createComponent(WhoAmIPage);
    TestBed.tick();
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

  it('shows loading until each answer arrives', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="token-loading"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="platform-loading"]')).not.toBeNull();

    http.expectOne('/api/me').flush(tokenView);
    http.expectOne('/api/identity/me').flush(platformView);
    await fixture.whenStable();
    expect(el.querySelector('[data-testid="token-loading"]')).toBeNull();
  });

  it('refreshes both views on demand', async () => {
    const fixture = await render();
    http.expectOne('/api/me').flush(tokenView);
    http.expectOne('/api/identity/me').flush(platformView);
    await fixture.whenStable();

    (fixture.nativeElement as HTMLElement).querySelector('button')!.click();
    TestBed.tick();
    http.expectOne('/api/me').flush({ ...tokenView, tenantId: 't-globex' });
    http.expectOne('/api/identity/me').flush({ ...platformView, tenantId: 't-globex' });
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="tenant"]')?.textContent).toContain('t-globex');
    expect(el.querySelector('[data-testid="platform-tenant"]')?.textContent).toContain('t-globex');
  });

  it('keeps the token view when the platform refuses (no membership)', async () => {
    const fixture = await render();
    http.expectOne('/api/me').flush({ ...tokenView, tenantId: null });
    http.expectOne('/api/identity/me').flush({ code: 'no_membership' }, { status: 403, statusText: 'Forbidden' });
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="me"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="platform-error"]')?.textContent).toContain('not a member of any tenant');
    expect(el.querySelector('[data-testid="verdict"]')?.textContent).toContain('No access');
  });

  it.each([
    ['t-acme', 't-acme', 'In agreement'],
    ['t-globex', 't-acme', 'Platform wins'],
    [null, 't-acme', 'Resolved'],
  ])('compares token tenant %s with platform tenant %s: %s', async (claim, granted, label) => {
    const fixture = await render();
    http.expectOne('/api/me').flush({ ...tokenView, tenantId: claim });
    http.expectOne('/api/identity/me').flush({ ...platformView, tenantId: granted });
    await fixture.whenStable();

    const verdict = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="verdict"]');
    expect(verdict?.textContent).toContain(label);
  });
});

describe('describeError', () => {
  it.each([
    [{ status: 409, error: { code: 'multiple_active_memberships' } }, 'several stores'],
    [{ status: 403, error: { code: 'tenant_not_permitted' } }, 'not a customer of this store'],
    [{ status: 401, error: null }, '401'],
    [{ status: 429, error: null }, 'rate limit'],
    [{ status: 0, error: null }, 'Could not reach'],
  ])('explains %o', (err, expected) => {
    expect(describeError(err)).toContain(expected);
  });
});
