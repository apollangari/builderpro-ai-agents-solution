import { Component, ElementRef, effect, input, output, signal, viewChild } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { describeError } from '../../core/api/describe-error';

/** The filter the concierge applied (server-side) and the products it matches. Mirrors CatalogFilterDto. */
export interface CatalogFilter {
  attributes: Record<string, string>;
  minPrice: number | null;
  maxPrice: number | null;
  words: string | null;
  skus: string[];
}

interface Turn {
  role: 'user' | 'assistant';
  content: string;
  guarded?: boolean;
}

/**
 * The chat beside the catalog (owner request): "white quartz under $90" -> the concierge picks a
 * filter, the server applies it to this ZIP's products of this type, and the page shows the matches.
 * It only filters what's shown: it starts nothing, and prices pass the same price guard.
 */
@Component({
  selector: 'bp-catalog-chat',
  template: `
    <section class="card chat" aria-label="Ask the concierge">
      <div class="head">
        <span class="dot" aria-hidden="true"></span>
        <h2 class="card-title">Ask the concierge</h2>
      </div>
      <div class="log" #log aria-live="polite" data-testid="catalog-log">
        @if (turns().length === 0) {
          <p class="msg">Tell me what you're after, like "white quartz under $90" or "something durable for a busy kitchen", and I'll narrow the list.</p>
          <div class="ideas">
            @for (s of ideas(); track s) { <button type="button" class="btn small" (click)="send(s)">{{ s }}</button> }
          </div>
        }
        @for (t of turns(); track $index) {
          <p class="msg" [class.user]="t.role === 'user'" [attr.data-testid]="'catalog-' + t.role">{{ t.content }}</p>
        }
        @if (reply.isLoading()) { <p class="msg"><span class="skeleton" style="width: 8rem"></span></p> }
        @if (reply.error()) { <div class="warning" role="alert"><span>{{ problem() }}</span></div> }
      </div>
      <form class="composer" (submit)="$event.preventDefault(); send(text())">
        <label class="sr-only" for="catalog-input">Describe what you want</label>
        <input id="catalog-input" type="text" maxlength="2000" autocomplete="off" placeholder="e.g. white quartz under $90"
               [value]="text()" (input)="text.set($any($event.target).value)" [disabled]="reply.isLoading()" data-testid="catalog-input" />
        <button type="submit" class="btn btn-primary" [disabled]="reply.isLoading() || !text().trim()" data-testid="catalog-send">Send</button>
      </form>
    </section>
  `,
  styles: `
    .chat { display: grid; gap: .6rem; }
    .head { display: flex; align-items: center; gap: .5rem; }
    .head .card-title { margin: 0; }
    .dot { width: .6rem; height: .6rem; border-radius: 50%; background: #2e7d32; }
    .log { display: grid; gap: .55rem; max-height: 52vh; overflow-y: auto; align-content: start; }
    .msg { margin: 0; max-width: 92%; padding: .6rem .85rem; border-radius: var(--bp-radius-md); background: var(--bp-surface-muted); white-space: pre-wrap; }
    .msg.user { justify-self: end; background: var(--bp-accent); color: var(--bp-on-accent); }
    .ideas { display: flex; flex-wrap: wrap; gap: .4rem; }
    .small { min-height: 2.25rem; font-size: .9rem; }
    .composer { display: flex; gap: .5rem; }
    .composer input { flex: 1; min-width: 0; min-height: var(--bp-tap); padding: 0 .8rem; border-radius: var(--bp-radius-md); border: 1px solid var(--bp-border-strong); background: var(--bp-surface); color: var(--bp-ink); font: inherit; }
  `,
})
export class CatalogChatComponent {
  readonly zip = input.required<string>();
  readonly category = input.required<string>();
  readonly categoryName = input('products');
  readonly filtered = output<CatalogFilter>();

  protected readonly turns = signal<Turn[]>([]);
  protected readonly text = signal('');
  protected readonly ideas = () => [`Show me the cheapest ${this.categoryName().toLowerCase()}`, 'What do you recommend for a busy family?'];

  private readonly request = signal<object | null>(null);
  protected readonly reply = httpResource<{ reply: string; guarded: boolean; filter: CatalogFilter | null }>(() => {
    const body = this.request();
    return body ? { url: '/api/assistant/concierge', method: 'POST', body } : undefined;
  });
  private readonly log = viewChild<ElementRef<HTMLElement>>('log');

  constructor() {
    effect(() => {
      if (!this.reply.hasValue()) return;
      const r = this.reply.value()!;
      this.turns.update((t) => [...t, { role: 'assistant', content: r.reply, guarded: r.guarded }]);
      if (r.filter) this.filtered.emit(r.filter);
      this.request.set(null);
    });
    effect(() => {
      this.turns();
      const el = this.log()?.nativeElement;
      if (el) queueMicrotask(() => (el.scrollTop = el.scrollHeight));
    });
  }

  send(message: string): void {
    const content = message.trim();
    if (!content || this.reply.isLoading()) return;
    const history = [...this.turns().filter((t) => !t.guarded || t.role === 'user'), { role: 'user' as const, content }];
    this.turns.update((t) => [...t, { role: 'user', content }]);
    this.text.set('');
    this.request.set({
      messages: history.slice(-20).map(({ role, content }) => ({ role, content })),
      catalog: { zip: this.zip(), category: this.category() },
    });
  }

  protected problem(): string {
    const code = (this.reply.error() as { error?: { code?: string } } | undefined)?.error?.code;
    if (code === 'assistant_unavailable') return "The concierge isn't set up yet. Use the filters instead.";
    if (code === 'assistant_busy') return 'The AI model is busy right now. Try again in a minute.';
    return describeError(this.reply.error() as never);
  }
}
