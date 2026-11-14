import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { StoreFeatures } from '../../core/tenant/store-features';
import { DeskPage } from './desk.page';

describe('DeskPage (3e)', () => {
  let http: HttpTestingController;
  const settle = async () => {
    TestBed.tick();
    await new Promise((r) => setTimeout(r));
    TestBed.tick();
  };

  function setup(support = true, scheduling = true) {
    TestBed.configureTestingModule({
      imports: [DeskPage],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: StoreFeatures, useValue: { supportChat: signal(support), scheduling: signal(scheduling), desk: signal(true), isCustomer: signal(false) } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(DeskPage);
    return fixture.nativeElement as HTMLElement;
  }

  const thread = (mode: 'concierge' | 'staff', extra: object[] = []) => ({
    id: 'c1', mode, staffName: mode === 'staff' ? 'Bob' : null,
    messages: [{ seq: 1, role: 'customer', text: 'Can someone call me?', author: null, at: '2026-11-14T10:00:00Z' }, ...extra],
  });

  it('takes a chat over, replies by name and hands it back', async () => {
    const el = setup();
    await settle();
    http.expectOne('/api/assistant/conversations').flush([
      { id: 'c1', customerId: 'c-alice', projectId: 'p1', mode: 'concierge', staffName: null, updatedAt: '2026-11-14T10:00:00Z', lastMessage: 'Can someone call me?', lastRole: 'customer' },
    ]);
    await settle();
    el.querySelector<HTMLButtonElement>('[data-testid="conversation"]')!.click();
    await settle();
    http.expectOne('/api/assistant/conversations/c1/messages').flush(thread('concierge'));
    await settle();
    expect(el.querySelector('[data-testid="thread"]')?.textContent).toContain('Can someone call me?');

    el.querySelector<HTMLButtonElement>('[data-testid="desk-takeover"]')!.click();
    await settle();
    http.expectOne((r) => r.url === '/api/assistant/conversations/c1/takeover' && r.method === 'POST')
      .flush(thread('staff', [{ seq: 2, role: 'system', text: 'Bob from the store has joined; the concierge is paused.', author: 'Bob', at: '2026-11-14T10:01:00Z' }]));
    await settle();

    const input = el.querySelector<HTMLInputElement>('[data-testid="desk-reply"]')!;
    input.value = "Sure, I'll call you at 3.";
    input.dispatchEvent(new Event('input'));
    await settle();
    el.querySelector<HTMLButtonElement>('[data-testid="desk-send"]')!.click();
    await settle();
    const post = http.expectOne((r) => r.url === '/api/assistant/conversations/c1/messages' && r.method === 'POST');
    expect(post.request.body).toEqual({ text: "Sure, I'll call you at 3." });
    post.flush(thread('staff', [
      { seq: 2, role: 'system', text: 'Bob joined', author: 'Bob', at: '2026-11-14T10:01:00Z' },
      { seq: 3, role: 'staff', text: "Sure, I'll call you at 3.", author: 'Bob', at: '2026-11-14T10:02:00Z' },
    ]));
    await settle();
    expect(el.querySelector('[data-testid="thread"]')?.textContent).toContain("Sure, I'll call you at 3.");

    el.querySelector<HTMLButtonElement>('[data-testid="desk-release"]')!.click();
    await settle();
    http.expectOne((r) => r.url === '/api/assistant/conversations/c1/release' && r.method === 'POST').flush(thread('concierge'));
    await settle();
    expect(el.querySelector('[data-testid="desk-takeover"]')).not.toBeNull();
    http.verify();
  });

  it('the work queue moves an installation on and records a visit measured as quoted', async () => {
    const el = setup(false, true);
    await settle();
    http.expectOne('/api/staff/queue').flush([
      { projectId: 'p1', title: 'Kitchen', customerId: 'c-alice', stage: 'measure', kind: 'measure', detail: 'Measure visit booked', visitStart: '2026-11-16T15:00:00Z', installStep: null, callId: null, measurements: { countertop: { countertop: [{ widthIn: 120, depthIn: 25 }] } }, updatedAt: '2026-11-14T10:00:00Z' },
      { projectId: 'p2', title: 'Bath', customerId: 'c-abe', stage: 'install', kind: 'install', detail: 'Installation scheduled', visitStart: null, installStep: 'scheduled', callId: null, measurements: null, updatedAt: '2026-11-14T10:00:00Z' },
    ]);
    await settle();
    expect(el.querySelector('[data-testid="tab-chats"]')).toBeNull();   // no support.chat: no Chats tab

    el.querySelector<HTMLButtonElement>('[data-testid="install-next"]')!.click();
    await settle();
    const step = http.expectOne((r) => r.url === '/api/projects/p2/install' && r.method === 'POST');
    expect(step.request.body).toEqual({ step: 'fabricating' });
    step.flush({});
    await settle();
    http.expectOne('/api/staff/queue').flush([]);
    await settle();
    expect(el.textContent).toContain('Nothing waiting');
    http.verify();
  });
});
