import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';
import { LogLevel, OpenIdConfiguration, StsConfigHttpLoader, StsConfigLoader } from 'angular-auth-oidc-client';
import { map } from 'rxjs';

/** Returned by GET /api/auth-config (anonymous). The SERVER decides which IdP the UI uses. */
export interface AuthConfigResponse {
  authority: string; // e.g. http://localhost:8080/realms/builderpro (Keycloak) or an Entra issuer
  clientId: string; // the PUBLIC client the browser uses (no secret: PKCE protects the code)
  scope: string;
}

/**
 * Builds the OIDC client configuration at startup from the API, so the same Angular build
 * works against Keycloak (dev) or Entra (prod) with zero rebuilds: generic OIDC.
 *
 * Flow: Authorization Code + PKCE (the only flow recommended for SPAs today).
 */
export function toOpenIdConfiguration(cfg: AuthConfigResponse, origin: string): OpenIdConfiguration {
  return {
    authority: cfg.authority,
    clientId: cfg.clientId,
    scope: cfg.scope,
    responseType: 'code', // Authorization Code + PKCE
    redirectUrl: origin,
    postLogoutRedirectUri: origin,
    silentRenew: true,
    useRefreshToken: true, // renew in the background without iframes
    renewTimeBeforeTokenExpiresInSeconds: 30,
    // The interceptor attaches the access token ONLY to these URLs. Never to third parties.
    secureRoutes: ['/api/'],
    logLevel: LogLevel.Warn,
  };
}

export function authConfigLoaderFactory(): StsConfigLoader {
  const http = inject(HttpClient);
  return new StsConfigHttpLoader(
    http
      .get<AuthConfigResponse>('/api/auth-config')
      .pipe(map((cfg) => toOpenIdConfiguration(cfg, window.location.origin))),
  );
}
