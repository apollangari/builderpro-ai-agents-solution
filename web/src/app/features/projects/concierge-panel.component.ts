import { Component, DestroyRef, ElementRef, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { describeError } from '../../core/api/describe-error';

interface Turn {
  role: 'user' | 'assistant' | 'staff' | 'system';
  content: string;
  guarded?: boolean;
  /** 3e: the person answering, for staff messages. */
  author?: string | null;
}

interface Reply {
  reply: string;
  guarded: boolean;
  toolsUsed: string[];
  projectChanged: boolean;
  /** 3e: the conversation (kept on the server so store staff can take over) and who answers now. */
  conversationId?: string | null;
  answering?: 'concierge' | 'staff';
  staffName?: string | null;
  lastSeq?: number;
}

/** 3e: what's new in the conversation (staff replies, joins and hand-backs). */
interface Thread {
  id: string;
  mode: 'concierge' | 'staff';
  staffName: string | null;
  messages: { seq: number; role: 'customer' | 'concierge' | 'staff' | 'system'; text: string; author: string | null; at: string }[];
}

/**
 * The workspace's concierge column (2n-f): the same concierge as /assistant, working on THIS
 * project (POST /api/assistant/concierge with projectId). When it saves answers or issues the quote,
 * the workspace re-reads the project. It never accepts: no tool for it, and the API refuses agents.
 */
@Component({
  selector: 'bp-concierge-panel',
  template: `
    <section class="card chat" aria-label="Concierge">
      <div class="head">
        <span class="dot" aria-hidden="true"></span>
        <h2 class="card-title" data-testid="panel-title">{{ staffName() ? staffName() + ' from the store' : helpOnly() ? 'Help' : 'Concierge' }}</h2>
        <span class="muted small" data-testid="panel-who">{{ staffName() ? 'a person is answering · the concierge is paused' : helpOnly() ? 'answers your questions · the steps are yours' : 'answers instantly · prepares, never accepts' }}</span>
      </div>
      <div class="log" #log aria-live="polite" data-testid="panel-log">
        @if (turns().length === 0) {
          <p class="msg">{{ helpOnly() ? 'Hi! Ask me anything about the products or the steps on this page.' : 'Hi! Tell me about your project, or ask about anything on this page. I can fill in the steps for you.' }}</p>
        }
        @for (t of turns(); track $index) {
          @if (t.role === 'system') {
            <p class="note" data-testid="panel-system">{{ t.content }}</p>
          } @else {
            <p class="msg" [class.user]="t.role === 'user'" [class.staff]="t.role === 'staff'" [attr.data-testid]="'panel-' + t.role">
              @if (t.role === 'staff') { <span class="author">{{ t.author }}</span> }{{ t.content }}</p>
          }
        }
        @if (reply.isLoading()) { <p class="msg"><span class="skeleton" style="width: 9rem"></span></p> }
        @if (reply.error()) { <div class="warning" role="alert"><span>{{ problem() }}</span></div> }
      </div>
      <form class="composer" (submit)="$event.preventDefault(); send(text())">
        <label class="sr-only" for="panel-input">Message the concierge</label>
        <input id="panel-input" type="text" maxlength="2000" autocomplete="off" placeholder="Ask anything about your project"
               [value]="text()" (input)="text.set($any($event.target).value)" [disabled]="reply.isLoading()" data-testid="panel-input" />
        <button type="submit" class="send" aria-label="Send" [disabled]="reply.isLoading() || !text().trim()" data-testid="panel-send"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 12l16-8-6 16-2-7-8-1z" /></svg></button>
      </form>
    </section>
  `,
  styles: `
    :host { display: block; }
    .chat { display: grid; gap: .75rem; }
    /* Wide screens: the chat stays beside you while the page scrolls, as tall as the screen allows
       (never taller than the work beside it), so Send is always in view; messages scroll inside. */
    @media (min-width: 961px) {
      :host { position: sticky; top: 1rem; align-self: start; height: min(100%, calc(100dvh - 17.5rem)); min-height: 20rem; }
      .chat { height: 100%; box-sizing: border-box; grid-template-rows: auto minmax(0, 1fr) auto; }
      .log { max-height: none; }
    }
    .head { display: flex; align-items: center; gap: .5rem; flex-wrap: wrap; }
    .head .card-title { margin: 0; }
    .dot { width: .6rem; height: .6rem; border-radius: 50%; background: #2e7d32; }
    .small { font-size: .85rem; }
    .log { display: grid; gap: .6rem; max-height: 55vh; overflow-y: auto; align-content: start; }
    .log > * { align-self: start; }
    .msg { margin: 0; max-width: 90%; padding: .65rem .9rem; border-radius: var(--bp-radius-md); background: var(--bp-surface-muted); white-space: pre-wrap; }
    .msg.user { justify-self: end; background: var(--bp-accent); color: var(--bp-on-accent); }
    .msg.staff { background: var(--bp-accent-soft); border-left: 3px solid var(--bp-accent); }
    .author { display: block; font-size: .75rem; font-weight: 700; margin-bottom: .15rem; }
    .note { margin: 0; justify-self: center; font-size: .8rem; color: var(--bp-ink-muted); }
    .composer { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: .4rem; align-items: center; }
    .send { width: 44px; height: 44px; border-radius: 50%; border: 0; display: grid; place-items: center; background: var(--bp-accent); color: var(--bp-on-accent); cursor: pointer; }
    .send:disabled { opacity: .45; cursor: default; }
    .send:focus-visible { outline: 2px solid var(--bp-accent); outline-offset: 2px; }
    .send svg { width: 20px; height: 20px; fill: currentColor; }
    .composer input { width: 100%; box-sizing: border-box; min-width: 0; min-height: var(--bp-tap); padding: 0 .8rem; border-radius: var(--bp-radius-md); border: 1px solid var(--bp-border-strong); background: var(--bp-surface); color: var(--bp-ink); font: inherit; }
  `,
})
export class ConciergePanelComponent {
  readonly projectId = input.required<string>();
  /** Text to start the next message with (e.g. "I have a question about quote Q-1042: "). */
  readonly askText = input<string | null>(null);
  /** The store's package has the help chat but not the concierge (1h): it answers, it doesn't fill in steps. */
  readonly helpOnly = input(false);
  readonly projectChanged = output<void>();

  protected readonly turns = signal<Turn[]>([]);
  protected readonly text = signal('');
  /** 3e: kept so the same thread continues, and staff replies arrive (polled). */
  private readonly conversationId = signal<string | null>(null);
  private readonly lastSeq = signal(0);
  protected readonly staffName = signal<string | null>(null);
  private readonly pollTick = signal(0);
  private readonly request = signal<{ messages: { role: string; content: string }[]; projectId: string; conversationId: string | null } | null>(null);
  protected readonly reply = httpResource<Reply>(() => {
    const body = this.request();
    return body ? { url: '/api/assistant/concierge', method: 'POST', body } : undefined;
  });
  private readonly log = viewChild<ElementRef<HTMLElement>>('log');
  protected readonly updates = httpResource<Thread>(() => {
    this.pollTick();
    const id = this.conversationId();
    return id ? `/api/assistant/conversations/${encodeURIComponent(id)}/messages?after=${this.lastSeq()}` : undefined;
  });

  constructor() {
    effect(() => {
      if (!this.reply.hasValue()) return;
      const r = this.reply.value()!;
      if (r.conversationId) this.conversationId.set(r.conversationId);
      if (r.lastSeq) this.lastSeq.set(Math.max(this.lastSeq(), r.lastSeq));
      this.staffName.set(r.answering === 'staff' ? (r.staffName ?? 'Store staff') : null);
      // While a person answers, there's no concierge reply: their answer arrives through the thread.
      if (r.answering !== 'staff') this.turns.update((t) => [...t, { role: 'assistant', content: r.reply, guarded: r.guarded }]);
      if (r.projectChanged) this.projectChanged.emit();
      this.request.set(null);
    });
    // 3e: new staff messages, joins and hand-backs (our own and the concierge's are already shown).
    effect(() => {
      if (!this.updates.hasValue()) return;
      const thread = this.updates.value()!;
      const fresh = thread.messages.filter((m) => m.seq > this.lastSeq());
      if (fresh.length) {
        this.lastSeq.set(fresh[fresh.length - 1].seq);
        const shown = fresh.filter((m) => m.role === 'staff' || m.role === 'system');
        if (shown.length) this.turns.update((t) => [...t, ...shown.map((m) => ({ role: m.role as 'staff' | 'system', content: m.text, author: m.author }))]);
      }
      this.staffName.set(thread.mode === 'staff' ? (thread.staffName ?? 'Store staff') : null);
    });
    const timer = setInterval(() => {
      if (this.conversationId() && !this.updates.isLoading() && !this.reply.isLoading()) this.pollTick.update((n) => n + 1);
    }, 4000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
    effect(() => {
      const ask = this.askText();
      if (ask) this.text.set(ask);
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
    // The model sees the customer and the concierge; staff lines go as the assistant side, system notes not at all.
    const history = [
      ...this.turns()
        .filter((t) => t.role !== 'system' && (!t.guarded || t.role === 'user'))
        .map((t) => ({ role: t.role === 'user' ? 'user' : 'assistant', content: t.content })),
      { role: 'user', content },
    ];
    this.turns.update((t) => [...t, { role: 'user', content }]);
    this.text.set('');
    this.request.set({ messages: history.slice(-20), projectId: this.projectId(), conversationId: this.conversationId() });
  }

  protected problem(): string {
    const code = (this.reply.error() as { error?: { code?: string } } | undefined)?.error?.code;
    if (code === 'assistant_unavailable') return "The concierge isn't set up yet (no AI model configured).";
    if (code === 'assistant_busy') return 'The AI model is busy right now. Try again in a minute.';
    return describeError(this.reply.error() as never);
  }
}
