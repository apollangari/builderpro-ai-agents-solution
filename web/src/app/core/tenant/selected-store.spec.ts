import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { SelectedStore, TENANT_HEADER, tenantHintInterceptor } from './selected-store';

describe('SelectedStore + tenantHintInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([tenantHintInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    backend.verify();
    sessionStorage.clear();
  });

  it('adds the picked store to /api/ calls', () => {
    TestBed.inject(SelectedStore).select('t-globex');

    http.get('/api/catalog/products').subscribe();

    expect(backend.expectOne('/api/catalog/products').request.headers.get(TENANT_HEADER)).toBe('t-globex');
  });

  it('never sends it anywhere else', () => {
    TestBed.inject(SelectedStore).select('t-globex');

    http.get('https://fonts.googleapis.com/css2').subscribe();

    expect(backend.expectOne('https://fonts.googleapis.com/css2').request.headers.has(TENANT_HEADER)).toBe(false);
  });

  it('sends nothing until a store is picked', () => {
    http.get('/api/me').subscribe();
    expect(backend.expectOne('/api/me').request.headers.has(TENANT_HEADER)).toBe(false);
  });

  it('remembers the pick for the tab and refuses malformed ids', () => {
    const store = TestBed.inject(SelectedStore);
    store.select('t-acme');
    expect(sessionStorage.getItem('bp.selectedTenant')).toBe('t-acme');

    store.select('t-acme\r\nX-Evil: 1');
    expect(store.tenantId()).toBeNull();
    expect(sessionStorage.getItem('bp.selectedTenant')).toBeNull();
  });
});
