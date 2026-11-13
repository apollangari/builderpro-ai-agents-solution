import { Injectable, InjectionToken, computed, effect, inject } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { SelectedStore } from './selected-store';
import { ThemeService } from '../theme/theme.service';

/** Domains whose first label is a store: acme.localhost (dev), acme.builderpro.everisys.com. */
export const STORE_BASE_DOMAINS = ['localhost', 'builderpro.everisys.com'];
const SLUG = /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/;
const NOT_STORES = new Set(['www', 'app', 'api', 'admin']);

/**
 * "acme.builderpro.everisys.com" -> "acme"; the plain domain, unknown domains and reserved labels
 * (www, app, api, admin) -> null. Pure, so the rules are unit tests.
 */
export function storeSlugFromHost(hostname: string, bases: readonly string[] = STORE_BASE_DOMAINS): string | null {
  const host = hostname.toLowerCase().replace(/\.$/, '');
  for (const base of bases) {
    if (!host.endsWith('.' + base)) continue;
    const label = host.slice(0, -(base.length + 1));
    return SLUG.test(label) && !NOT_STORES.has(label) ? label : null;
  }
  return null;
}

/** The page's host name; a token so tests can be "on" acme.localhost. */
export const HOSTNAME = new InjectionToken<string>('HOSTNAME', { providedIn: 'root', factory: () => window.location.hostname });

/** GET /api/identity/stores/{id} (anonymous). Mirrors StoreInfoDto in C#. */
export interface StoreInfo {
  id: string;
  name: string;
  tenantId: string;
  signupEnabled: boolean;
}

/**
 * The store this address belongs to (1g). On a store's subdomain the app shows that store's name
 * and sends its tenant as the X-BuilderPro-Tenant HINT on every API call; the store picker is
 * hidden. The server still checks membership, so the subdomain never grants access by itself.
 */
@Injectable({ providedIn: 'root' })
export class HostStore {
  private readonly selected = inject(SelectedStore);
  private readonly theme = inject(ThemeService);

  readonly slug = storeSlugFromHost(inject(HOSTNAME));
  readonly info = httpResource<StoreInfo>(() => (this.slug ? `/api/identity/stores/${encodeURIComponent(this.slug)}` : undefined));

  /** True on a store's subdomain, even before its info arrives. */
  readonly active = this.slug !== null;
  /** The address names a store that doesn't exist. */
  readonly unknown = computed(() => this.active && !!this.info.error());
  readonly tenantId = computed(() => (this.info.hasValue() ? this.info.value()!.tenantId : null));

  constructor() {
    effect(() => {
      if (!this.info.hasValue()) return;
      const store = this.info.value()!;
      if (this.selected.tenantId() !== store.tenantId) this.selected.select(store.tenantId);
      this.theme.apply({ retailerName: store.name, projectLabel: this.theme.projectLabel() });
    });
  }
}
