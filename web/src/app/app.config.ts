import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { StsConfigLoader, authInterceptor, provideAuth, withAppInitializerAuthCheck } from 'angular-auth-oidc-client';
import { routes } from './app.routes';
import { authConfigLoaderFactory } from './core/auth/auth.config';
import { tenantHintInterceptor } from './core/tenant/selected-store';

// Angular 22 is zoneless by default: no zone.js, change detection is driven by signals.
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    // authInterceptor adds "Authorization: Bearer <token>" to secureRoutes (/api/) only.
    // tenantHintInterceptor adds the store picked in the header (1f) to /api/ calls.
    provideHttpClient(withFetch(), withInterceptors([authInterceptor(), tenantHintInterceptor])),
    // OIDC: config loaded from the API at startup; the app initializer completes the
    // login callback (?code=...&state=...) before the first route renders.
    provideAuth(
      { loader: { provide: StsConfigLoader, useFactory: authConfigLoaderFactory } },
      withAppInitializerAuthCheck(),
    ),
  ],
};
