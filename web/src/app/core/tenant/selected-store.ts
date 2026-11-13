import { HttpInterceptorFn } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';

const KEY = 'bp.selectedTenant';
const TENANT_ID = /^[A-Za-z0-9_-]{1,128}$/;
export const TENANT_HEADER = 'X-BuilderPro-Tenant';

/**
 * The store a multi-store customer chose in the picker (1f). Only a HINT: the server accepts it
 * only if it matches one of the caller's active memberships. Per tab (sessionStorage).
 */
@Injectable({ providedIn: 'root' })
export class SelectedStore {
  readonly tenantId = signal<string | null>(SelectedStore.read());

  select(tenantId: string | null): void {
    const value = tenantId && TENANT_ID.test(tenantId) ? tenantId : null;
    this.tenantId.set(value);
    try {
      if (value) sessionStorage.setItem(KEY, value);
      else sessionStorage.removeItem(KEY);
    } catch {
      /* storage blocked: the choice lasts until reload */
    }
  }

  private static read(): string | null {
    try {
      const v = sessionStorage.getItem(KEY);
      return v && TENANT_ID.test(v) ? v : null;
    } catch {
      return null;
    }
  }
}

/** Adds the chosen store to our own API calls only (relative /api/ URLs), never to third parties. */
export const tenantHintInterceptor: HttpInterceptorFn = (req, next) => {
  const tenant = inject(SelectedStore).tenantId();
  return tenant && req.url.startsWith('/api/') ? next(req.clone({ setHeaders: { [TENANT_HEADER]: tenant } })) : next(req);
};
