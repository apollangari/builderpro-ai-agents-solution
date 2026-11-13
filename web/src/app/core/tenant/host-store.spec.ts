import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { HOSTNAME, HostStore, storeSlugFromHost } from './host-store';
import { SelectedStore } from './selected-store';
import { ThemeService } from '../theme/theme.service';

describe('storeSlugFromHost (1g)', () => {
  it.each([
    ['acme.localhost', 'acme'],
    ['globex.builderpro.everisys.com', 'globex'],
    ['ACME.localhost', 'acme'],
    ['localhost', null],
    ['builderpro.everisys.com', null],
    ['www.builderpro.everisys.com', null],
    ['a.b.localhost', null], // one label only
    ['acme.example.com', null], // not our domain
    ['-bad.localhost', null],
  ])('%s -> %s', (host, slug) => {
    expect(storeSlugFromHost(host)).toBe(slug);
  });
});

describe('HostStore', () => {
  let http: HttpTestingController;

  function setup(hostname: string) {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: HOSTNAME, useValue: hostname }],
    });
    http = TestBed.inject(HttpTestingController);
    return TestBed.inject(HostStore);
  }

  afterEach(() => http.verify());

  const settle = async () => {
    TestBed.tick();
    await new Promise((r) => setTimeout(r));
    TestBed.tick();
  };

  it("on a store's subdomain: names the store, brands the header and sends its tenant as the hint", async () => {
    const host = setup('acme.localhost');
    expect(host.active).toBe(true);
    await settle();
    http.expectOne('/api/identity/stores/acme').flush({ id: 'acme', name: 'Acme Home Improvement', tenantId: 't-acme', signupEnabled: true });
    await settle();

    expect(TestBed.inject(SelectedStore).tenantId()).toBe('t-acme');
    expect(TestBed.inject(ThemeService).retailerName()).toBe('Acme Home Improvement');
    expect(host.unknown()).toBe(false);
  });

  it('an unknown store address is flagged and chooses nothing', async () => {
    const host = setup('nope.localhost');
    await settle();
    http.expectOne('/api/identity/stores/nope').flush({ code: 'store_not_found' }, { status: 404, statusText: 'Not Found' });
    await settle();

    expect(host.unknown()).toBe(true);
    expect(TestBed.inject(SelectedStore).tenantId()).toBeNull();
  });

  it('the plain domain asks for nothing', async () => {
    const host = setup('localhost');
    await settle();
    expect(host.active).toBe(false);
  });
});
