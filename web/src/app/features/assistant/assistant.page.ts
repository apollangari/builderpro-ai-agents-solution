import { Component, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { CurrencyPipe, DecimalPipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { Router } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { describeError } from '../../core/api/describe-error';
import { GROUP_TITLES, GuidedLine, GuidedQuote, ProductOptions, missingLabels } from '../quote/guided';

export interface ChatTurn {
  role: 'user' | 'assistant';
  content: string;
  toolsUsed?: string[];
  guarded?: boolean;
}

/** POST /api/assistant/concierge answer. Mirrors ConciergeReply in C#. */
interface ConciergeReply {
  reply: string;
  guarded: boolean;
  toolsUsed: string[];
  draft: DraftRequest | null;
  quote: GuidedQuote | null;
}

/** The guided request the concierge priced: answers only, never prices (POST /api/quotes/guided body). */
interface DraftRequest {
  zip: string;
  sku: string;
  measurements: unknown;
  choices: unknown[];
}

const TOOL_LABELS: Record<string, string> = {
  check_zip: 'checked your ZIP',
  list_project_types: 'looked up project types',
  find_products: 'searched products for your area',
  get_project_steps: 'looked up the steps',
  price_draft: 'priced your draft',
};

/**
 * The concierge (3b): a conversation on the left builds a draft guided quote, shown on the right
 * exactly as the pricing engine priced it. The concierge can prepare the draft; only the customer
 * can save it ("Save quote" posts the draft to /api/quotes/guided). Signal-first: each send is an
 * httpResource POST of the whole conversation plus the current draft (the server keeps no state).
 */
@Component({
  selector: 'bp-assistant-page',
  imports: [CurrencyPipe, DecimalPipe],
  template: `
    <div class="page">
      <header class="page-head">
        <span class="eyebrow">Concierge</span>
        <h1>Plan your project</h1>
        <p>Tell the concierge about your project. It builds a draft quote with your store's prices; you review and save it.</p>
      </header>

      <div class="layout">
        <section class="card chat" aria-label="Conversation">
          <div class="log" #log aria-live="polite" data-testid="chat-log">
            @if (turns().length === 0) {
              <div class="suggestions">
                @for (s of suggestions; track s) {
                  <button type="button" class="btn" (click)="send(s)">{{ s }}</button>
                }
              </div>
            }
            @for (t of turns(); track $index) {
              <div class="msg" [class.user]="t.role === 'user'" [attr.data-testid]="'msg-' + t.role">
                <p>{{ t.content }}</p>
                @if (t.toolsUsed?.length) {
                  <div class="chips">
                    @for (tool of t.toolsUsed; track $index) {
                      <span class="badge">{{ label(tool) }}</span>
                    }
                  </div>
                }
              </div>
            }
            @if (reply.isLoading()) {
              <div class="msg" data-testid="thinking"><span class="skeleton" style="width: 12rem"></span></div>
            }
            @if (reply.error()) {
              <div class="warning" role="alert" data-testid="chat-error"><span>{{ problem() }}</span></div>
            }
          </div>

          <form class="composer" (submit)="$event.preventDefault(); send(draftText())">
            <label class="sr-only" for="chat-input">Your message</label>
            <input id="chat-input" type="text" maxlength="2000" autocomplete="off" placeholder="e.g. Quartz countertops for my kitchen, about 10 ft"
                   [value]="draftText()" (input)="draftText.set($any($event.target).value)" [disabled]="reply.isLoading()" data-testid="chat-input" />
            <button type="submit" class="btn btn-primary" [disabled]="reply.isLoading() || !draftText().trim()" data-testid="chat-send">Send</button>
          </form>
        </section>

        <aside class="card draft" aria-label="Draft quote" data-testid="draft-panel">
          <h2 class="card-title">Your draft quote</h2>
          @if (quote(); as q) {
            <p class="muted">ZIP {{ draft()!.zip }} · priced by your store's pricing engine</p>
            @for (grp of groups(q.lines); track grp.group) {
              <h3 class="group-title">{{ titles[grp.group] }}</h3>
              @for (l of grp.lines; track l.sku) {
                <div class="line" data-testid="draft-line">
                  <span>{{ l.name }} <span class="muted">{{ l.quantity | number: '1.0-3' }} {{ unit(l.unit) }}</span></span>
                  <span>{{ l.lineTotal | currency: q.currency }}</span>
                </div>
              }
            }
            <dl class="totals">
              <dt>Subtotal</dt><dd>{{ q.subtotal | currency: q.currency }}</dd>
              <dt>Tax</dt><dd>{{ q.tax | currency: q.currency }}</dd>
              <dt class="total">Total</dt><dd class="total" data-testid="draft-total">{{ q.total | currency: q.currency }}</dd>
            </dl>
            @if (open().length) {
              <p class="warning" data-testid="draft-missing"><span>Still to choose: {{ open().join(', ') }}.</span></p>
            }
            <button type="button" class="btn btn-primary" [disabled]="!q.complete || saved.isLoading()" (click)="save()" data-testid="draft-save">
              {{ saved.isLoading() ? 'Saving…' : 'Save quote' }}
            </button>
            <p class="muted small">You confirm every step yourself: the concierge can prepare a quote, but never saves, accepts or pays for you.</p>
            @if (saved.error()) {
              <div class="warning" role="alert"><span>{{ describe(saved.error()) }}</span></div>
            }
          } @else {
            <p class="muted" data-testid="draft-empty">Your draft appears here as soon as the concierge has a product and measurements.</p>
          }
        </aside>
      </div>
    </div>
  `,
  styles: `
    .layout { display: grid; gap: 1rem; grid-template-columns: minmax(0, 3fr) minmax(0, 2fr); align-items: start; }
    @media (max-width: 860px) { .layout { grid-template-columns: 1fr; } }
    .chat { display: grid; gap: 1rem; }
    .log { display: grid; gap: .75rem; max-height: 60vh; overflow-y: auto; padding: .25rem; }
    .msg { max-width: 85%; padding: .75rem 1rem; border-radius: var(--bp-radius-md); background: var(--bp-surface-muted); }
    .msg p { margin: 0; white-space: pre-wrap; }
    .msg.user { justify-self: end; background: var(--bp-accent); color: var(--bp-on-accent); }
    .msg .chips { margin-top: .5rem; display: flex; flex-wrap: wrap; gap: .35rem; }
    .suggestions { display: flex; flex-wrap: wrap; gap: .5rem; }
    .composer { display: flex; gap: .6rem; }
    .composer input {
      flex: 1; min-height: var(--bp-tap); padding: 0 .9rem; border-radius: var(--bp-radius-md);
      border: 1px solid var(--bp-border-strong); background: var(--bp-surface); color: var(--bp-ink); font: inherit;
    }
    .draft { display: grid; gap: .5rem; position: sticky; top: 1rem; }
    .group-title { margin: .5rem 0 0; font-size: .9rem; color: var(--bp-ink-muted); }
    .line { display: flex; justify-content: space-between; gap: 1rem; padding: .3rem 0; border-bottom: 1px solid var(--bp-border); }
    .totals { display: grid; grid-template-columns: 1fr auto; gap: .2rem 1rem; margin: .5rem 0; }
    .totals dd { margin: 0; text-align: right; }
    .totals .total { font-weight: 700; font-size: 1.1rem; }
    .small { font-size: .85rem; }
  `,
})
export class AssistantPage {
  private readonly oidc = inject(OidcSecurityService);
  private readonly router = inject(Router);

  protected readonly turns = signal<ChatTurn[]>([]);
  protected readonly draftText = signal('');
  protected readonly draft = signal<DraftRequest | null>(null);
  protected readonly quote = signal<GuidedQuote | null>(null);
  protected readonly titles = GROUP_TITLES;
  protected readonly suggestions = [
    'I want new countertops for my kitchen',
    'I need a new roof',
    'What sheds do you have?',
  ];

  /** The ZIP from the sign-up address, so the concierge can ask "Is this project at ZIP …?". */
  private readonly accountZip = computed(() => {
    const claims = this.oidc.userData().userData as { postal_code?: string } | null;
    return claims?.postal_code?.trim() || null;
  });

  private readonly request = signal<{ messages: { role: string; content: string }[]; accountZip: string | null; draft: DraftRequest | null } | null>(null);
  protected readonly reply = httpResource<ConciergeReply>(() => {
    const body = this.request();
    return body ? { url: '/api/assistant/concierge', method: 'POST', body } : undefined;
  });

  /** Step labels for "still to choose", from the draft product's option steps. */
  private readonly steps = httpResource<ProductOptions>(() => {
    const d = this.draft();
    return d ? `/api/catalog/products/${encodeURIComponent(d.sku)}/options?zip=${encodeURIComponent(d.zip)}` : undefined;
  });
  protected readonly open = computed(() => {
    const q = this.quote();
    if (!q) return [];
    const groups = this.steps.hasValue() ? this.steps.value()!.groups : [];
    const labels = missingLabels(groups, q.missing);
    return labels.length ? labels : q.missing;
  });

  /** Save: the CUSTOMER posts the draft to the quote API. The concierge has no way to do this. */
  private readonly saveRequest = signal<DraftRequest | null>(null);
  protected readonly saved = httpResource<{ id: string }>(() => {
    const body = this.saveRequest();
    return body ? { url: '/api/quotes/guided', method: 'POST', body } : undefined;
  });

  private readonly log = viewChild<ElementRef<HTMLElement>>('log');

  constructor() {
    // An answer arrived: add it, keep the newest priced draft, close the request.
    effect(() => {
      // hasValue() first: reading value() of a resource in an error state throws.
      if (!this.reply.hasValue()) return;
      const r = this.reply.value();
      this.turns.update((t) => [...t, { role: 'assistant', content: r.reply, toolsUsed: r.toolsUsed, guarded: r.guarded }]);
      if (r.draft && r.quote) {
        this.draft.set(r.draft);
        this.quote.set(r.quote);
      }
      this.request.set(null);
    });
    effect(() => {
      if (this.saved.hasValue()) void this.router.navigate(['/quotes'], { queryParams: { saved: this.saved.value()!.id } });
    });
    // Keep the newest message in view.
    effect(() => {
      this.turns();
      const el = this.log()?.nativeElement;
      if (el) queueMicrotask(() => (el.scrollTop = el.scrollHeight));
    });
  }

  send(text: string): void {
    const content = text.trim();
    if (!content || this.reply.isLoading()) return;
    const history = [...this.turns().filter((t) => !t.guarded || t.role === 'user'), { role: 'user' as const, content }];
    this.turns.set([...this.turns(), { role: 'user', content }]);
    this.draftText.set('');
    // Last 20 turns, plain text only (the server validates the same limits).
    this.request.set({
      messages: history.slice(-20).map(({ role, content }) => ({ role, content })),
      accountZip: this.accountZip(),
      draft: this.draft(),
    });
  }

  protected save(): void {
    if (this.quote()?.complete && this.draft()) this.saveRequest.set(this.draft());
  }

  protected groups(lines: GuidedLine[]): { group: GuidedLine['group']; lines: GuidedLine[] }[] {
    const order: GuidedLine['group'][] = ['product', 'minimum', 'options', 'protection'];
    return order.map((group) => ({ group, lines: lines.filter((l) => l.group === group) })).filter((g) => g.lines.length);
  }

  protected label(tool: string): string {
    return TOOL_LABELS[tool] ?? tool;
  }

  protected unit(u: string): string {
    return u === 'sqft' ? 'sq ft' : u;
  }

  protected describe(err: unknown): string {
    return describeError(err as Parameters<typeof describeError>[0]);
  }

  protected problem(): string {
    const err = this.reply.error() as { error?: { code?: string } } | undefined;
    switch (err?.error?.code) {
      case 'assistant_unavailable':
        return "The concierge isn't set up yet (no AI model configured).";
      case 'assistant_busy':
        return 'The AI model is busy right now. Try again in a minute.';
      case 'assistant_model_error':
        return "The AI model couldn't answer. Try again later.";
      default:
        return describeError(this.reply.error());
    }
  }
}
