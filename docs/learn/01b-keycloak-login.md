# LEARN 1b: Sign-in with Keycloak (OIDC, Authorization Code + PKCE)

**Time:** about 90 minutes · **Goal:** understand exactly what happens between clicking
**Sign in** and the API trusting the request, and be able to debug every step.

## 1. What changed in 1b

| Piece | Change |
|---|---|
| AppHost | Keycloak container (port 8080) with an imported realm; injects `Auth__Authority` into the API |
| `infra/keycloak/realms/builderpro-realm.json` | Realm: clients `builderpro-web` (public, PKCE) and `builderpro-api` (audience), roles, 3 users in 2 tenants, `tenant_id` and `roles` claim mappers |
| Me API | JWT bearer validation (generic OIDC); `/api/auth-config` (anonymous); `/api/me` now **requires** a token and returns tenant + roles |
| Angular | `angular-auth-oidc-client`: config loaded from the API, sign-in/out in the header, `/me` protected by a guard, interceptor adds the token to `/api/*` only |

## 2. Concepts

### OIDC in one paragraph
Your app never sees passwords. It **redirects** the browser to the identity provider (IdP:
Keycloak here, Entra in production). The user signs in *there*, and the IdP redirects back with a
one-time **code**. The app swaps the code for **tokens**:
- **ID token:** *who the user is*, for the UI. Never send it to APIs.
- **Access token:** *what this caller may do at this API*. The API validates it on every request.
- **Refresh token:** gets new access tokens quietly when they expire (5 minutes here).

### Why Authorization Code + PKCE
A browser app can't keep a secret, so its client is **public**. PKCE protects the code. The app
creates a random `code_verifier`, sends only its hash (`code_challenge`) in the redirect, and must
present the original verifier to redeem the code. A stolen code is useless on its own. Implicit
flow and password grant are **disabled** in the realm on purpose.

### What the API checks (`AuthSetup.cs`)
| Check | Claim | Why |
|---|---|---|
| Signature | (JWT signature) | The token came from our IdP. Keys come from its JWKS endpoint, cached |
| Issuer | `iss` | From the issuer we trust, not any IdP |
| Audience | `aud` | Issued **for this API**. A token for another app is rejected |
| Lifetime | `exp`, `nbf` | Not expired; 1-minute clock skew allowed |

### Generic OIDC (Keycloak below prod, Entra in prod)
Nothing in the code says "Keycloak". The API reads `Auth:Authority` / `Audience` / `TenantClaim`
/ `RoleClaim` from configuration, and Angular loads its IdP settings from `/api/auth-config` at
startup. Switching to Entra is configuration only:

| Setting | Keycloak (local) | Entra (Azure) |
|---|---|---|
| `Auth:Authority` | `http://localhost:8080/realms/builderpro` | `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| `Auth:Audience` | `builderpro-api` | `api://<api-app-id>` (or the API app's client id) |
| `Auth:WebClientId` | `builderpro-web` | the SPA app registration's client id |
| `Auth:WebScope` | `openid profile email` | `openid profile email api://<api-app-id>/access_as_user` |
| `Auth:TenantClaim` | `tenant_id` (user-attribute mapper) | `extn.tenant_id` or an app-role convention (decided in 1d) |
| `Auth:RoleClaim` | `roles` (realm-role mapper) | `roles` (app roles) |

### Tenant comes from the token, never from the request
The UI never sends "my tenant is X". The tenant is a **claim** set by an admin in the IdP (the
attribute is admin-edit only in the realm's user profile). In 1d your Identity service's
membership store becomes the source of truth.

## 3. Code tour

| # | File | Notice |
|---|---|---|
| 1 | `src/AppHost/.../AppHost.cs` | Keycloak container; why ports 8080 and 4200 are fixed; `ReferenceExpression` builds the authority URL |
| 2 | `infra/keycloak/realms/builderpro-realm.json` | Public client + PKCE S256; audience mapper; `tenant_id` mapper; implicit/password grants off |
| 3 | `src/Services/Me/.../Auth/AuthOptions.cs` | Every IdP-specific value is configuration |
| 4 | `.../Auth/AuthSetup.cs` | Fail-fast; `MapInboundClaims = false`; name/role claim types |
| 5 | `.../MeEndpoints.cs` | `AllowAnonymous` vs `RequireAuthorization` |
| 6 | `web/src/app/core/auth/auth.config.ts` | Config loaded from the API; `secureRoutes` limits where tokens go |
| 7 | `web/src/app/app.config.ts` | `provideAuth` + `withAppInitializerAuthCheck` + `authInterceptor` |
| 8 | `web/src/app/app.routes.ts` | `autoLoginPartialRoutesGuard` on `/me` |
| 9 | `tests/.../TestAuth.cs` | A test auth scheme: test authorization without an IdP |

## 4. Debugging labs

> Start with `dotnet run --project src/AppHost/BuilderPro.AppHost` (Docker must be running),
> then open http://localhost:4200.

### Lab A: watch the whole login in the browser (15 min)
1. Open DevTools → **Network**, and tick *Preserve log*.
2. Click **Sign in** and log in as `alice` / `Passw0rd!`.
3. Find, in order:
   - `GET /api/auth-config`: where the app learned its IdP.
   - `.../.well-known/openid-configuration`: the IdP's endpoints (discovery).
   - `.../protocol/openid-connect/auth?...code_challenge=...&code_challenge_method=S256`: the redirect, with PKCE.
   - The redirect back to `localhost:4200/?code=...&state=...`.
   - `POST .../protocol/openid-connect/token`, with `code_verifier` in the form data.
   - `GET /api/me` with `Authorization: Bearer eyJ...`.

### Lab B: read your token (10 min)
1. Copy the bearer token from the `/api/me` request.
2. Decode it locally. Don't paste real tokens into websites:
   ```bash
   echo '<token>' | cut -d. -f2 | tr '_-' '/+' | base64 -d 2>/dev/null; echo
   ```
3. Find `iss`, `aud` (contains `builderpro-api`), `exp`, `sub`, `tenant_id`, `roles`.
4. Sign out, sign in as `carol`, and compare `tenant_id`.

### Lab C: break the audience and watch the API refuse (10 min)
1. Keycloak admin (http://localhost:8080, admin/admin) → realm **builderpro** → Clients →
   `builderpro-web` → **Client scopes** → `builderpro-web-dedicated` → delete the
   *audience: builderpro-api* mapper.
2. Sign out and sign in again, then open **Who am I**. You get **401**.
3. The API logs say why, and the response has a `WWW-Authenticate: Bearer error="invalid_token", error_description="The audience ... is invalid"` header.
4. Put the mapper back (or restart the AppHost to re-import the realm).

**Lesson:** the audience check is what stops a token issued for *another* app being used
against *yours*.

### Lab D: step through token validation (15 min)
1. In `AuthSetup.cs`, add temporary JwtBearer events inside `AddJwtBearer`:
   ```csharp
   jwt.Events = new JwtBearerEvents
   {
       OnTokenValidated = ctx => { var c = ctx.Principal; return Task.CompletedTask; },  // breakpoint here
       OnAuthenticationFailed = ctx => { var e = ctx.Exception; return Task.CompletedTask; } // and here
   };
   ```
2. Run the AppHost under the debugger and open **Who am I**. Inspect `ctx.Principal.Claims`.
3. Repeat Lab C and stop in `OnAuthenticationFailed`, then read `e.Message`.
4. Remove the events when done; don't log full tokens.

### Lab E: expiry and silent refresh (10 min)
Access tokens live 5 minutes. Stay on **Who am I**, wait about 5 minutes, and watch Network: a
`POST .../token` with `grant_type=refresh_token` happens around 30 s before expiry, without a
page reload.

### Lab F: test without an IdP (5 min)
Run `dotnet test`, then read `TestAuth.cs`. `X-Test-User` / `X-Test-Tenant` headers stand in
for a token, so authorization logic is tested fast and offline. Ask yourself why this test
scheme must never be registered outside tests.

## 5. Exercises

1. **Staff-only endpoint.** Add `GET /api/me/staff-area` requiring role `staff`
   (`.RequireAuthorization(p => p.RequireRole("staff"))`). Test: alice gets 403, bob gets 200.
   Then add a page that shows a friendly 403 message.
2. **A fourth user.** Add `dave` (tenant `t-globex`, role `staff`) to the realm file, restart,
   and log in.
3. **Missing tenant.** Remove alice's `tenant_id`. What should `/api/me` do? Decide (401? 403?
   empty tenant?), write the test first, then implement it. *Hint:* 1d formalises this.
4. **Logout everywhere.** Sign in in two tabs, then sign out in one. What does the other tab do?
   Read the library's `checkSession` option and decide whether you want it.

## 6. Review questions

1. *Why is `builderpro-web` a public client with no secret?*
   > Anything shipped to a browser is readable by the user. PKCE replaces the secret for
   > redeeming the code.
2. *Why must the API check `aud`?*
   > Without it, any valid token from the same IdP (issued to another app) would be accepted.
3. *Why load the IdP settings from `/api/auth-config` instead of an Angular environment file?*
   > One build for all environments; the server owns the configuration; switching
   > Keycloak ↔ Entra needs no rebuild.
4. *The UI hides the "Who am I" link when signed out. Is that security?*
   > No. It's UX. Security is the API returning 401/403. Always assume the UI is bypassed.
5. *Why `MapInboundClaims = false`?*
   > ASP.NET's legacy behaviour renames `sub` to a long WS-* URI. Turning it off keeps claim
   > names identical to the token, which avoids a whole class of "claim not found" bugs.

## 7. Further reading

- OAuth 2.0 for Browser-Based Apps (IETF BCP): https://datatracker.ietf.org/doc/draft-ietf-oauth-browser-based-apps/
- PKCE (RFC 7636): https://www.rfc-editor.org/rfc/rfc7636
- Keycloak server administration (realms, clients, mappers): https://www.keycloak.org/documentation
- `angular-auth-oidc-client` docs: https://angular-auth-oidc-client.com
- ASP.NET Core JWT bearer authentication: https://learn.microsoft.com/aspnet/core/security/authentication/configure-jwt-bearer-authentication
