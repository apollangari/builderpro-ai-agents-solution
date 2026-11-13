import { Component } from '@angular/core';
import { CurrencyPipe, DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { describeError } from '../../core/api/describe-error';
import { ProjectSummary, STAGES } from '../quote/guided';

/** My projects (2n-f): each job with its stage and latest quote. Reached from the account menu. */
@Component({
  selector: 'bp-projects-page',
  imports: [CurrencyPipe, DatePipe, RouterLink],
  template: `
    <div class="page">
      <header class="page-head">
        <span class="eyebrow">Projects</span>
        <h1>My projects</h1>
      </header>
      @if (projects.error()) {
        <div class="warning" role="alert"><span>{{ describe(projects.error()) }}</span></div>
      } @else if (projects.hasValue() && projects.value().length === 0) {
        <div class="card"><p>No projects yet. <a routerLink="/quote/new">Start a project</a>.</p></div>
      } @else {
        <div class="list">
          @for (p of projects.value(); track p.id) {
            <a class="card row" [routerLink]="['/projects', p.id]" data-testid="project-row">
              <div>
                <strong>{{ p.title }}</strong>
                <span class="muted">ZIP {{ p.zip }} · {{ stageLabel(p.stage) }} · updated {{ p.updatedAt | date: 'medium' }}@if (p.preparedBy) { · prepared by {{ p.preparedBy }} }</span>
              </div>
              @if (p.quoteNumber) {
                <span class="total">Q-{{ p.quoteNumber }} · {{ p.quoteTotal | currency: p.currency ?? 'USD' }}</span>
              }
            </a>
          }
        </div>
      }
      <div class="page-actions"><a routerLink="/quote/new" class="btn btn-primary">Start a project</a></div>
    </div>
  `,
  styles: `
    .list { display: grid; gap: .6rem; margin-bottom: 1rem; }
    .row { display: flex; justify-content: space-between; align-items: center; gap: 1rem; color: inherit; text-decoration: none; }
    .row:hover { border-color: var(--bp-accent); }
    .row div { display: grid; gap: .15rem; }
    .muted { color: var(--bp-ink-muted); font-size: .85rem; }
    .total { font-weight: 700; color: var(--bp-accent); }
  `,
})
export class ProjectsPage {
  protected readonly projects = httpResource<ProjectSummary[]>(() => '/api/projects', { defaultValue: [] });
  protected readonly describe = describeError;

  protected stageLabel(stage: string): string {
    return STAGES.find((s) => s.id === stage)?.label ?? stage;
  }
}
