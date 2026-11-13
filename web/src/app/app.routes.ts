import { Routes } from '@angular/router';
import { autoLoginPartialRoutesGuard } from 'angular-auth-oidc-client';

export const routes: Routes = [
  // Public: anyone can see it.
  { path: '', loadComponent: () => import('./features/home/home.page').then((m) => m.HomePage) },
  // Protected: not signed in -> the guard sends you to the identity provider, then back here.
  {
    path: 'me',
    canActivate: [autoLoginPartialRoutesGuard],
    loadComponent: () => import('./features/whoami/whoami.page').then((m) => m.WhoAmIPage),
  },
  {
    path: 'catalog',
    canActivate: [autoLoginPartialRoutesGuard],
    loadComponent: () => import('./features/catalog/catalog.page').then((m) => m.CatalogPage),
  },
  {
    path: 'assistant',
    canActivate: [autoLoginPartialRoutesGuard],
    loadComponent: () => import('./features/assistant/assistant.page').then((m) => m.AssistantPage),
  },
  {
    path: 'projects',
    canActivate: [autoLoginPartialRoutesGuard],
    loadComponent: () => import('./features/projects/projects.page').then((m) => m.ProjectsPage),
  },
  {
    path: 'projects/:id',
    canActivate: [autoLoginPartialRoutesGuard],
    loadComponent: () => import('./features/projects/workspace.page').then((m) => m.WorkspacePage),
  },
  {
    path: 'quote/new',
    canActivate: [autoLoginPartialRoutesGuard],
    loadComponent: () => import('./features/quote/new-quote.page').then((m) => m.NewQuotePage),
  },
  {
    path: 'quote',
    canActivate: [autoLoginPartialRoutesGuard],
    loadComponent: () => import('./features/quote/quote.page').then((m) => m.QuotePage),
  },
  {
    path: 'quotes',
    canActivate: [autoLoginPartialRoutesGuard],
    loadComponent: () => import('./features/quote/quotes.page').then((m) => m.QuotesPage),
  },
  {
    path: 'visualize',
    canActivate: [autoLoginPartialRoutesGuard],
    loadComponent: () => import('./features/visualize/visualize.page').then((m) => m.VisualizePage),
  },
  {
    path: 'compare',
    canActivate: [autoLoginPartialRoutesGuard],
    loadComponent: () => import('./features/compare/compare.page').then((m) => m.ComparePage),
  },
  // Store sign-up link (public): /signup?store=acme -> Create account -> /welcome joins the store.
  { path: 'signup', loadComponent: () => import('./features/signup/signup.page').then((m) => m.SignupPage) },
  {
    path: 'welcome',
    canActivate: [autoLoginPartialRoutesGuard],
    loadComponent: () => import('./features/signup/welcome.page').then((m) => m.WelcomePage),
  },
  { path: '**', redirectTo: '' },
];
