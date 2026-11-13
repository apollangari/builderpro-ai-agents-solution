import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AssistantPage } from './assistant.page';

describe('AssistantPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [AssistantPage], providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  const settle = async () => {
    await new Promise((r) => setTimeout(r));
    TestBed.tick();
  };

  async function ask(fixture: { nativeElement: HTMLElement; whenStable(): Promise<unknown> }, text: string) {
    const el = fixture.nativeElement;
    const input = el.querySelector<HTMLInputElement>('[data-testid="chat-input"]')!;
    input.value = text;
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    el.querySelector<HTMLButtonElement>('[data-testid="chat-send"]')!.click();
    await settle();
  }

  it('sends the whole conversation and shows the answer with the tools it used', async () => {
    const fixture = TestBed.createComponent(AssistantPage);
    await fixture.whenStable();

    await ask(fixture, 'Which countertops do you have?');
    const first = http.expectOne('/api/assistant/chat');
    expect(first.request.body).toEqual({ messages: [{ role: 'user', content: 'Which countertops do you have?' }] });
    first.flush({ reply: 'We have quartz at $89.00 per sq ft.', guarded: false, toolsUsed: ['search_products'] });
    await settle();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="msg-assistant"]')?.textContent).toContain('$89.00');
    expect(el.querySelector('[data-testid="msg-assistant"]')?.textContent).toContain('searched the catalog');

    await ask(fixture, 'And granite?');
    const second = http.expectOne('/api/assistant/chat');
    expect(second.request.body.messages.map((m: { role: string }) => m.role)).toEqual(['user', 'assistant', 'user']);
    second.flush({ reply: 'Granite is $72.00.', guarded: false, toolsUsed: ['search_products'] });
    await settle();
    expect(el.querySelectorAll('[data-testid="msg-assistant"]').length).toBe(2);
  });

  it('a blocked (guarded) answer is shown but not sent back as history', async () => {
    const fixture = TestBed.createComponent(AssistantPage);
    await fixture.whenStable();

    await ask(fixture, 'Roughly what do countertops cost?');
    http.expectOne('/api/assistant/chat').flush({ reply: "I can't confirm that price right now.", guarded: true, toolsUsed: [] });
    await settle();

    await ask(fixture, 'Price 30 sq ft of quartz');
    const next = http.expectOne('/api/assistant/chat');
    expect(next.request.body.messages.map((m: { role: string }) => m.role)).toEqual(['user', 'user']);
    next.flush({ reply: 'ok', guarded: false, toolsUsed: [] });
    await settle();
  });

  it('explains when the assistant is not configured', async () => {
    const fixture = TestBed.createComponent(AssistantPage);
    await fixture.whenStable();

    await ask(fixture, 'Hi');
    http.expectOne('/api/assistant/chat').flush({ code: 'assistant_unavailable' }, { status: 503, statusText: 'Service Unavailable' });
    await settle();

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="chat-error"]')?.textContent).toContain("isn't set up");
  });
});
