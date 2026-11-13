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
  // Store sign-up link (public): /signup?store=acme -> Create account -> /welcome joins the store.
  { path: 'signup', loadComponent: () => import('./features/signup/signup.page').then((m) => m.SignupPage) },
  {
    path: 'welcome',
    canActivate: [autoLoginPartialRoutesGuard],
    loadComponent: () => import('./features/signup/welcome.page').then((m) => m.WelcomePage),
  },
  { path: '**', redirectTo: '' },
];
