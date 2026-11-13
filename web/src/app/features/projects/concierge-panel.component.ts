import { Component, ElementRef, effect, input, output, signal, viewChild } from '@angular/core';
import { httpResource } from '@angular/common/http';
import { describeError } from '../../core/api/describe-error';

interface Turn {
  role: 'user' | 'assistant';
  content: string;
  guarded?: boolean;
}

interface Reply {
  reply: string;
  guarded: boolean;
  toolsUsed: string[];
  projectChanged: boolean;
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
        <h2 class="card-title">Concierge</h2>
        <span class="muted small">answers instantly · prepares, never accepts</span>
      </div>
      <div class="log" #log aria-live="polite" data-testid="panel-log">
        @if (turns().length === 0) {
          <p class="msg">Hi! Tell me about your project, or ask about anything on this page. I can fill in the steps for you.</p>
        }
        @for (t of turns(); track $index) {
          <p class="msg" [class.user]="t.role === 'user'" [attr.data-testid]="'panel-' + t.role">{{ t.content }}</p>
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
    .chat { display: grid; gap: .75rem; }
    .head { display: flex; align-items: center; gap: .5rem; flex-wrap: wrap; }
    .head .card-title { margin: 0; }
    .dot { width: .6rem; height: .6rem; border-radius: 50%; background: #2e7d32; }
    .small { font-size: .85rem; }
    .log { display: grid; gap: .6rem; max-height: 55vh; overflow-y: auto; }
    .msg { margin: 0; max-width: 90%; padding: .65rem .9rem; border-radius: var(--bp-radius-md); background: var(--bp-surface-muted); white-space: pre-wrap; }
    .msg.user { justify-self: end; background: var(--bp-accent); color: var(--bp-on-accent); }
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
  readonly projectChanged = output<void>();

  protected readonly turns = signal<Turn[]>([]);
  protected readonly text = signal('');
  private readonly request = signal<{ messages: { role: string; content: string }[]; projectId: string } | null>(null);
  protected readonly reply = httpResource<Reply>(() => {
    const body = this.request();
    return body ? { url: '/api/assistant/concierge', method: 'POST', body } : undefined;
  });
  private readonly log = viewChild<ElementRef<HTMLElement>>('log');

  constructor() {
    effect(() => {
      if (!this.reply.hasValue()) return;
      const r = this.reply.value()!;
      this.turns.update((t) => [...t, { role: 'assistant', content: r.reply, guarded: r.guarded }]);
      if (r.projectChanged) this.projectChanged.emit();
      this.request.set(null);
    });
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
    const history = [...this.turns().filter((t) => !t.guarded || t.role === 'user'), { role: 'user' as const, content }];
    this.turns.update((t) => [...t, { role: 'user', content }]);
    this.text.set('');
    this.request.set({ messages: history.slice(-20).map(({ role, content }) => ({ role, content })), projectId: this.projectId() });
  }

  protected problem(): string {
    const code = (this.reply.error() as { error?: { code?: string } } | undefined)?.error?.code;
    if (code === 'assistant_unavailable') return "The concierge isn't set up yet (no AI model configured).";
    if (code === 'assistant_busy') return 'The AI model is busy right now. Try again in a minute.';
    return describeError(this.reply.error() as never);
  }
}
