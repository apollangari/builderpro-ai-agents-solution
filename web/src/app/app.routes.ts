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
  { path: '**', redirectTo: '' },
];
