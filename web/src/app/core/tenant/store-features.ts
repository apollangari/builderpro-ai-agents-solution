import { Injectable, computed, inject } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { OidcSecurityService } from 'angular-auth-oidc-client';

export type StoreFeature = 'guided' | 'help-chat' | 'concierge' | 'phone-scheduling';

/**
 * What the current store's package includes (1h): from GET /api/identity/me, for the store this
 * request is for (store picker / subdomain hint applied). The page only hides what isn't included;
 * the Assistant service refuses it anyway (feature_not_included).
 *
 * Until the answer arrives nothing optional is shown, so a chat never flashes up and disappears.
 */
@Injectable({ providedIn: 'root' })
export class StoreFeatures {
  private readonly oidc = inject(OidcSecurityService);
  private readonly me = httpResource<{ features?: StoreFeature[] }>(() =>
    this.oidc.authenticated().isAuthenticated ? '/api/identity/me' : undefined,
  );

  readonly known = computed(() => this.me.hasValue());
  readonly features = computed<StoreFeature[]>(() => (this.me.hasValue() ? (this.me.value()!.features ?? []) : []));

  readonly concierge = computed(() => this.features().includes('concierge'));
  readonly helpChat = computed(() => this.features().includes('help-chat'));
  /** Some chat is offered: the AI concierge, or the help chat that only answers. */
  readonly anyChat = computed(() => this.concierge() || this.helpChat());
}
