import { Component, ElementRef, effect, signal, viewChild } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { describeError } from '../../core/api/describe-error';

export interface ChatTurn {
  role: 'user' | 'assistant';
  content: string;
  toolsUsed?: string[];
  guarded?: boolean;
}

/** POST /api/assistant/chat answer. Mirrors ChatReply in C#. */
interface ChatReply {
  reply: string;
  guarded: boolean;
  toolsUsed: string[];
}

const TOOL_LABELS: Record<string, string> = {
  list_categories: 'looked up categories',
  search_products: 'searched the catalog',
  estimate_roof: 'ran the roof calculator',
  price_products: 'priced it with your store',
};

/**
 * The read-only product assistant (3a). Signal-first: the conversation is a signal, and each send is
 * an httpResource POST of the whole conversation (the server keeps no chat state). Prices in replies
 * always come from platform tools; the server blocks any it can't back up.
 */
@Component({
  selector: 'bp-assistant-page',
  template: `
    <div class="page">
      <header class="page-head">
        <span class="eyebrow">Assistant</span>
        <h1>Ask about products</h1>
        <p>Ask what fits your project. Prices come straight from your store; the assistant can't place orders.</p>
      </header>

      <section class="card chat">
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

        <form class="composer" (submit)="$event.preventDefault(); send(draft())">
          <label class="sr-only" for="chat-input">Your question</label>
          <input id="chat-input" type="text" maxlength="2000" autocomplete="off" placeholder="e.g. Which countertop suits a busy kitchen?"
                 [value]="draft()" (input)="draft.set($any($event.target).value)" [disabled]="reply.isLoading()" data-testid="chat-input" />
          <button type="submit" class="btn btn-primary" [disabled]="reply.isLoading() || !draft().trim()" data-testid="chat-send">Send</button>
        </form>
      </section>
    </div>
  `,
  styles: `
    .chat { display: grid; gap: 1rem; max-width: 52rem; }
    .log { display: grid; gap: .75rem; max-height: 60vh; overflow-y: auto; padding: .25rem; }
    .msg { max-width: 85%; padding: .75rem 1rem; border-radius: var(--bp-radius-md); background: var(--bp-surface-muted); }
    .msg p { margin: 0; white-space: pre-wrap; }
    .msg.user { justify-self: end; background: var(--bp-accent); color: var(--bp-on-accent); }
    .msg .chips { margin-top: .5rem; }
    .suggestions { display: flex; flex-wrap: wrap; gap: .5rem; }
    .composer { display: flex; gap: .6rem; }
    .composer input {
      flex: 1; min-height: var(--bp-tap); padding: 0 .9rem; border-radius: var(--bp-radius-md);
      border: 1px solid var(--bp-border-strong); background: var(--bp-surface); color: var(--bp-ink); font: inherit;
    }
  `,
})
export class AssistantPage {
  protected readonly turns = signal<ChatTurn[]>([]);
  protected readonly draft = signal('');
  protected readonly suggestions = [
    'Which countertops do you have?',
    'How much is 30 sq ft of the Calacatta quartz?',
    'I have a 2,000 sq ft footprint roof at 6/12. What do I need?',
  ];

  /** The conversation sent with the current question; null = nothing in flight. */
  private readonly request = signal<{ messages: { role: string; content: string }[] } | null>(null);
  protected readonly reply = httpResource<ChatReply>(() =>
    this.request() ? { url: '/api/assistant/chat', method: 'POST', body: this.request() } : undefined,
  );

  private readonly log = viewChild<ElementRef<HTMLElement>>('log');

  constructor() {
    // An answer arrived: add it to the conversation and close the request.
    effect(() => {
      // hasValue() first: reading value() of a resource in an error state throws.
      if (!this.reply.hasValue()) return;
      const r = this.reply.value();
      this.turns.update((t) => [...t, { role: 'assistant', content: r.reply, toolsUsed: r.toolsUsed, guarded: r.guarded }]);
      this.request.set(null);
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
    const turns = [...this.turns().filter((t) => !t.guarded || t.role === 'user'), { role: 'user' as const, content }];
    this.turns.set([...this.turns(), { role: 'user', content }]);
    this.draft.set('');
    // Last 20 turns, plain text only (the server validates the same limits).
    this.request.set({ messages: turns.slice(-20).map(({ role, content }) => ({ role, content })) });
  }

  protected label(tool: string): string {
    return TOOL_LABELS[tool] ?? tool;
  }

  protected problem(): string {
    const err = this.reply.error() as { error?: { code?: string } } | undefined;
    switch (err?.error?.code) {
      case 'assistant_unavailable':
        return "The assistant isn't set up yet (no AI model configured).";
      case 'assistant_busy':
        return 'The AI model is busy right now. Try again in a minute.';
      case 'assistant_model_error':
        return "The AI model couldn't answer. Try again later.";
      default:
        return describeError(this.reply.error());
    }
  }
}
