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
  private readonly me = httpResource<{ features?: StoreFeature[]; scopes?: string[]; customerId?: string | null }>(() =>
    this.oidc.authenticated().isAuthenticated ? '/api/identity/me' : undefined,
  );

  readonly known = computed(() => this.me.hasValue());
  readonly features = computed<StoreFeature[]>(() => (this.me.hasValue() ? (this.me.value()!.features ?? []) : []));

  readonly concierge = computed(() => this.features().includes('concierge'));
  readonly helpChat = computed(() => this.features().includes('help-chat'));
  /** Some chat is offered: the AI concierge, or the help chat that only answers. */
  readonly anyChat = computed(() => this.concierge() || this.helpChat());

  /** The caller's scopes in this store (staff and salespeople differ from customers). */
  readonly scopes = computed<string[]>(() => (this.me.hasValue() ? (this.me.value()!.scopes ?? []) : []));
  /** A customer of this store (accepts and pays). Staff and salespeople don't. */
  readonly isCustomer = computed(() => this.me.hasValue() && !!this.me.value()!.customerId);
  /** 1i: prepares projects for customers. */
  readonly salesperson = computed(() => !this.isCustomer() && this.scopes().includes('sales'));
  /** 1i: the Sales page (salesperson, or a manager who reads the commission report). */
  readonly salesDesk = computed(() => this.salesperson() || (!this.isCustomer() && this.scopes().includes('sales.report')));
  /** 3e: store staff who answer customer chats. */
  readonly supportChat = computed(() => !this.isCustomer() && this.scopes().includes('support.chat'));
  /** 3e/6b/6c: store staff who measure, schedule and install. */
  readonly scheduling = computed(() => !this.isCustomer() && this.scopes().includes('scheduling.write'));
  /** The store desk (chats and/or the work queue). */
  readonly desk = computed(() => this.supportChat() || this.scheduling());
}
