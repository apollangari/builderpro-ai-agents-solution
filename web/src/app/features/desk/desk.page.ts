import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { describeError } from '../../core/api/describe-error';
import { StoreFeatures } from '../../core/tenant/store-features';

export interface ConversationSummary {
  id: string;
  customerId: string;
  projectId: string | null;
  mode: 'concierge' | 'staff';
  staffName: string | null;
  updatedAt: string;
  lastMessage: string | null;
  lastRole: string | null;
}

export interface Thread {
  id: string;
  mode: 'concierge' | 'staff';
  staffName: string | null;
  messages: { seq: number; role: 'customer' | 'concierge' | 'staff' | 'system'; text: string; author: string | null; at: string }[];
}

export interface QueueItem {
  projectId: string;
  title: string;
  customerId: string | null;
  stage: string;
  kind: 'measure' | 'install' | 'call';
  detail: string;
  visitStart: string | null;
  installStep: 'scheduled' | 'fabricating' | 'installing' | 'done' | null;
  callId: string | null;
  measurements: unknown;
  updatedAt: string;
}

const NEXT_STEP: Record<string, { step: string; label: string }> = {
  scheduled: { step: 'fabricating', label: 'Mark as being made' },
  fabricating: { step: 'installing', label: 'Mark installing' },
  installing: { step: 'done', label: 'Mark done' },
};

/**
 * 3e: the store desk. Chats: the store's live concierge conversations; take one over (the concierge
 * pauses), reply under your name, hand it back. Work: visits to measure, installations to move on, and
 * calls where the customer asked for a person. Every action is the API's own, with its rules.
 */
@Component({
  selector: 'bp-desk-page',
  imports: [DatePipe, RouterLink],
  template: `
    <div class="page">
      <header class="page-head">
        <span class="eyebrow">Store desk</span>
        <h1>{{ tab() === 'chats' ? 'Customer chats' : 'Work to do' }}</h1>
      </header>
      <div class="tabs" role="tablist">
        @if (features.supportChat()) {
          <button type="button" role="tab" [attr.aria-selected]="tab() === 'chats'" [class.on]="tab() === 'chats'" (click)="tab.set('chats')" data-testid="tab-chats">Chats</button>
        }
        @if (features.scheduling()) {
          <button type="button" role="tab" [attr.aria-selected]="tab() === 'work'" [class.on]="tab() === 'work'" (click)="tab.set('work')" data-testid="tab-work">Work</button>
        }
      </div>

      @if (tab() === 'chats') {
        <div class="split">
          <section class="card list" aria-label="Conversations">
            @if (conversations.error()) { <div class="warning" role="alert"><span>{{ describe(conversations.error()) }}</span></div> }
            @for (c of conversations.value() ?? []; track c.id) {
              <button type="button" class="row" [class.on]="c.id === selected()" (click)="open(c.id)" data-testid="conversation">
                <span class="who">Customer {{ c.customerId }} @if (c.mode === 'staff') { <span class="badge">{{ c.staffName }}</span> }</span>
                <span class="muted small">{{ c.lastMessage }}</span>
                <span class="muted small">{{ c.updatedAt | date: 'short' }}</span>
              </button>
            } @empty {
              <p class="muted">No conversations in the last two days.</p>
            }
          </section>
          <section class="card thread" aria-label="Conversation">
            @if (thread(); as t) {
              <div class="log" data-testid="thread">
                @for (m of t.messages; track m.seq) {
                  @if (m.role === 'system') { <p class="note">{{ m.text }}</p> }
                  @else {
                    <p class="msg" [class.customer]="m.role === 'customer'" [class.staff]="m.role === 'staff'">
                      <span class="author">{{ m.role === 'customer' ? 'Customer' : m.role === 'staff' ? m.author : 'Concierge' }}</span>{{ m.text }}
                    </p>
                  }
                }
              </div>
              @if (t.mode === 'staff') {
                <form class="composer" (submit)="$event.preventDefault(); reply()">
                  <input type="text" maxlength="2000" [value]="text()" (input)="text.set($any($event.target).value)" placeholder="Reply as yourself" aria-label="Reply" data-testid="desk-reply" />
                  <button type="submit" class="btn btn-primary" [disabled]="!text().trim() || action.isLoading()" data-testid="desk-send">Send</button>
                </form>
                <div class="actions"><button type="button" class="btn" (click)="act('release')" [disabled]="action.isLoading()" data-testid="desk-release">Hand back to the concierge</button></div>
              } @else {
                <div class="actions"><button type="button" class="btn btn-primary" (click)="act('takeover')" [disabled]="action.isLoading()" data-testid="desk-takeover">Take over</button></div>
              }
              @if (action.error()) { <div class="warning" role="alert"><span>{{ describe(action.error()) }}</span></div> }
            } @else {
              <p class="muted">Pick a conversation.</p>
            }
          </section>
        </div>
      } @else {
        <section class="card" aria-label="Work queue">
          @if (queue.error()) { <div class="warning" role="alert"><span>{{ describe(queue.error()) }}</span></div> }
          <ul class="queue">
            @for (q of queue.value() ?? []; track q.projectId + q.kind + (q.callId ?? '')) {
              <li data-testid="queue-item" [attr.data-kind]="q.kind">
                <div>
                  <strong>{{ q.title }}</strong> <span class="muted small">· customer {{ q.customerId }}</span>
                  <p class="small">{{ q.detail }} @if (q.visitStart) { ({{ q.visitStart | date: 'EEE MMM d, h:mm a' }}) }</p>
                </div>
                <div class="actions">
                  @switch (q.kind) {
                    @case ('measure') {
                      <button type="button" class="btn btn-primary" (click)="measuredAsQuoted(q)" [disabled]="work.isLoading()" data-testid="measured-as-quoted">Measured as quoted</button>
                    }
                    @case ('install') {
                      @if (!q.installStep) {
                        <input type="date" [value]="installDate()" (input)="installDate.set($any($event.target).value)" aria-label="Installation date" data-testid="install-date" />
                        <button type="button" class="btn btn-primary" (click)="schedule(q)" [disabled]="!installDate() || work.isLoading()" data-testid="install-schedule">Schedule</button>
                      } @else if (next(q); as n) {
                        <button type="button" class="btn btn-primary" (click)="advance(q, n.step)" [disabled]="work.isLoading()" data-testid="install-next">{{ n.label }}</button>
                      }
                    }
                    @case ('call') {
                      <a class="btn" [routerLink]="['/projects', q.projectId]">Open project</a>
                    }
                  }
                </div>
              </li>
            } @empty {
              <li class="muted">Nothing waiting. Nice.</li>
            }
          </ul>
          @if (work.error()) { <div class="warning" role="alert"><span>{{ describe(work.error()) }}</span></div> }
          <p class="muted small">Different sizes on site? Record them through the API for now; a measurement form here comes next.</p>
        </section>
      }
    </div>
  `,
  styles: `
    .tabs { display: flex; gap: .4rem; margin-bottom: .8rem; }
    .tabs button { border: 1px solid var(--bp-border-strong); background: var(--bp-surface); border-radius: 999px; padding: .4rem 1rem; cursor: pointer; font: inherit; }
    .tabs button.on { background: var(--bp-accent); color: var(--bp-on-accent); border-color: var(--bp-accent); }
    .split { display: grid; gap: 1rem; grid-template-columns: minmax(16rem, 1fr) minmax(0, 2fr); align-items: start; }
    @media (max-width: 860px) { .split { grid-template-columns: 1fr; } }
    .list { display: grid; gap: .3rem; max-height: 70vh; overflow-y: auto; }
    .row { display: grid; gap: .15rem; text-align: left; padding: .6rem .7rem; border-radius: var(--bp-radius-md); border: 1px solid transparent; background: none; font: inherit; cursor: pointer; }
    .row.on, .row:hover { border-color: var(--bp-border-strong); background: var(--bp-surface-muted); }
    .who { font-weight: 600; }
    .badge { font-size: .75rem; padding: .05rem .45rem; border-radius: 999px; background: var(--bp-accent-soft); color: var(--bp-accent); }
    .thread { display: grid; gap: .7rem; }
    .log { display: grid; gap: .5rem; max-height: 55vh; overflow-y: auto; align-content: start; }
    .msg { margin: 0; max-width: 85%; padding: .55rem .8rem; border-radius: var(--bp-radius-md); background: var(--bp-surface-muted); white-space: pre-wrap; }
    .msg.customer { justify-self: start; }
    .msg.staff, .msg:not(.customer) { justify-self: end; }
    .msg.staff { background: var(--bp-accent-soft); }
    .author { display: block; font-size: .72rem; font-weight: 700; margin-bottom: .1rem; }
    .note { margin: 0; justify-self: center; font-size: .8rem; color: var(--bp-ink-muted); }
    .composer { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: .4rem; }
    .composer input, .actions input { min-height: var(--bp-tap); padding: 0 .7rem; border-radius: var(--bp-radius-md); border: 1px solid var(--bp-border-strong); font: inherit; box-sizing: border-box; min-width: 0; }
    .actions { display: flex; gap: .5rem; justify-content: flex-end; align-items: center; flex-wrap: wrap; }
    .queue { list-style: none; margin: 0; padding: 0; display: grid; gap: .6rem; }
    .queue li { display: flex; justify-content: space-between; gap: 1rem; align-items: center; padding: .7rem .8rem; border: 1px solid var(--bp-border); border-radius: var(--bp-radius-md); }
    .queue p { margin: .2rem 0 0; }
    .small { font-size: .85rem; }
  `,
})
export class DeskPage {
  protected readonly features = inject(StoreFeatures);
  protected readonly tab = signal<'chats' | 'work'>('chats');
  protected readonly selected = signal<string | null>(null);
  protected readonly text = signal('');
  protected readonly installDate = signal('');
  private readonly tick = signal(0);

  protected readonly conversations = httpResource<ConversationSummary[]>(() => {
    this.tick();
    return this.tab() === 'chats' && this.features.supportChat() ? '/api/assistant/conversations' : undefined;
  });
  private readonly threadRead = httpResource<Thread>(() => {
    this.tick();
    const id = this.selected();
    return id ? `/api/assistant/conversations/${encodeURIComponent(id)}/messages` : undefined;
  });
  private readonly request = signal<{ kind: 'takeover' | 'release' | 'reply'; text?: string; n: number } | null>(null);
  private n = 0;
  protected readonly action = httpResource<Thread>(() => {
    const r = this.request();
    const id = this.selected();
    if (!r || !id) return undefined;
    const base = `/api/assistant/conversations/${encodeURIComponent(id)}`;
    return r.kind === 'reply' ? { url: `${base}/messages`, method: 'POST', body: { text: r.text } } : { url: `${base}/${r.kind}`, method: 'POST', body: {} };
  });
  private readonly latest = signal<Thread | null>(null);
  protected readonly thread = computed(() => {
    const read = this.threadRead.hasValue() ? this.threadRead.value() : null;
    const acted = this.latest();
    if (!read) return acted;
    if (!acted || acted.id !== read.id) return read;
    return read.messages.length >= acted.messages.length ? read : acted;
  });

  protected readonly queue = httpResource<QueueItem[]>(() => {
    this.tick();
    return this.tab() === 'work' && this.features.scheduling() ? '/api/staff/queue' : undefined;
  });
  private readonly workRequest = signal<{ url: string; body: unknown; n: number } | null>(null);
  protected readonly work = httpResource<unknown>(() => {
    const w = this.workRequest();
    return w ? { url: w.url, method: 'POST', body: w.body } : undefined;
  });

  constructor() {
    effect(() => {
      if (!this.features.supportChat() && this.features.scheduling()) this.tab.set('work');
    });
    effect(() => {
      if (this.action.hasValue()) this.latest.set(this.action.value()!);
    });
    effect(() => {
      if (this.work.hasValue()) this.tick.update((t) => t + 1);   // re-read the queue after an action
    });
    const timer = setInterval(() => this.tick.update((t) => t + 1), 5000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  protected open(id: string): void {
    this.latest.set(null);
    this.selected.set(id);
  }

  protected act(kind: 'takeover' | 'release'): void {
    this.request.set({ kind, n: ++this.n });
  }

  protected reply(): void {
    const text = this.text().trim();
    if (!text) return;
    this.request.set({ kind: 'reply', text, n: ++this.n });
    this.text.set('');
  }

  protected next(q: QueueItem) {
    return q.installStep ? (NEXT_STEP[q.installStep] ?? null) : null;
  }

  protected measuredAsQuoted(q: QueueItem): void {
    this.workRequest.set({ url: `/api/projects/${q.projectId}/visit/measured`, body: { measurements: q.measurements }, n: ++this.n });
  }

  protected schedule(q: QueueItem): void {
    // Installation day at 9:00 store time is a sensible default; the date is what staff choose.
    this.workRequest.set({ url: `/api/projects/${q.projectId}/install`, body: { step: 'scheduled', scheduledFor: `${this.installDate()}T15:00:00Z` }, n: ++this.n });
  }

  protected advance(q: QueueItem, step: string): void {
    this.workRequest.set({ url: `/api/projects/${q.projectId}/install`, body: { step }, n: ++this.n });
  }

  protected describe(err: unknown): string {
    return describeError(err as Parameters<typeof describeError>[0]);
  }
}
