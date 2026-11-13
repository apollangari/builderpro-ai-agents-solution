import { Component, computed, input, output } from '@angular/core';

export interface TrackerStep {
  id: string;
  label: string;
  /** The customer may jump to this step (e.g. back to Measure). */
  clickable?: boolean;
}

/**
 * The journey tracker (owner: "arrow marks progress like a pizza tracker"): centred arrow segments.
 * Steps ahead are a light tint of the store's accent, finished steps carry a tick, and the step the
 * customer is on is the store's bold accent. Uses only the --bp-accent* tokens, so each store's
 * colour shows through (1g white-label).
 */
@Component({
  selector: 'bp-progress-tracker',
  template: `
    <nav class="wrap" aria-label="Progress">
      <ol class="tracker" data-testid="tracker">
        @for (s of steps(); track s.id; let i = $index) {
          <li [class.done]="i < currentIndex()" [class.current]="i === currentIndex()" [attr.aria-current]="i === currentIndex() ? 'step' : null"
              [attr.data-testid]="'track-' + s.id">
            @if (s.clickable && i !== currentIndex()) {
              <button type="button" (click)="selected.emit(s.id)">
                @if (i < currentIndex()) { <span class="tick" aria-hidden="true">✓</span> }{{ s.label }}
              </button>
            } @else {
              <span class="label">
                @if (i < currentIndex()) { <span class="tick" aria-hidden="true">✓</span> }{{ s.label }}
              </span>
            }
          </li>
        }
      </ol>
    </nav>
  `,
  styles: `
    :host { display: block; margin: 0 0 1.25rem; }
    .wrap { display: flex; justify-content: center; overflow-x: auto; padding: .25rem 0; }
    .tracker { display: flex; list-style: none; margin: 0; padding: 0; min-width: max-content; }
    li {
      --notch: 16px;
      display: flex; align-items: center; min-height: 3.25rem; margin-left: -6px;
      background: var(--bp-accent-soft); color: var(--bp-accent);
      clip-path: polygon(0 0, calc(100% - var(--notch)) 0, 100% 50%, calc(100% - var(--notch)) 100%, 0 100%, var(--notch) 50%);
      font-size: 1rem; opacity: .75;
    }
    li:first-child { margin-left: 0; clip-path: polygon(0 0, calc(100% - var(--notch)) 0, 100% 50%, calc(100% - var(--notch)) 100%, 0 100%); }
    li.done { opacity: 1; }
    li.current { background: var(--bp-accent); color: var(--bp-on-accent); font-weight: 700; opacity: 1; }
    .label, button {
      display: flex; align-items: center; gap: .35rem; height: 100%; padding: 0 1.5rem 0 calc(var(--notch) + .9rem);
      white-space: nowrap; font: inherit; color: inherit; background: none; border: 0;
    }
    li:first-child .label, li:first-child button { padding-left: 1.2rem; }
    button { cursor: pointer; text-decoration: underline; text-underline-offset: 3px; }
    button:focus-visible { outline: 2px solid var(--bp-accent); outline-offset: -4px; }
    .tick { font-weight: 700; }
    @media (max-width: 720px) { li { min-height: 2.6rem; font-size: .9rem; } .label, button { padding: 0 1rem 0 calc(var(--notch) + .5rem); } }
  `,
})
export class ProgressTrackerComponent {
  readonly steps = input.required<TrackerStep[]>();
  readonly current = input.required<string>();
  readonly selected = output<string>();
  protected readonly currentIndex = computed(() => this.steps().findIndex((s) => s.id === this.current()));
}
