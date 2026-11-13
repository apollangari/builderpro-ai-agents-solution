# LEARN 1e: Sign-up, combined login and Google

**Time:** about 40 minutes · **Goal:** let customers create their own accounts through a store's
link, sign in with a password or Google, and land as a customer of the right retailer.

## 1. The flow

```text
/signup?store=acme  (Angular, public)
   │ remembers "acme" (sessionStorage), calls authorize(prompt=create)
   ▼
Keycloak "Create your BuilderPro account"  (Navy & Sand theme)
   │ name, email, password, phone, contact consent, contact hours, project address
   │ ...or "Continue with Google" (same profile fields asked once, on first sign-in)
   ▼
back to the app, signed in → shell sees the pending store → /welcome
   │ POST /api/identity/onboarding { store: "acme" }
   ▼
Identity creates a CUSTOMER membership for t-acme → catalog, quotes work
```

## 2. What lives where

| Piece | Where |
|---|---|
| Login, sign-up, password pages | Keycloak, theme `infra/keycloak/themes/builderpro` (CSS on top of `keycloak.v2`) |
| Sign-up fields and their rules | Realm user profile (`components` in `builderpro-realm.json`): required, patterns, options |
| Google | Realm identity provider `google`, enabled only when the AppHost has both user secrets |
| Which stores accept sign-ups | `Identity:Stores` in Identity's `appsettings.json` |
| Joining a store | `POST /api/identity/onboarding` (`Onboarding.cs`) |

## 3. Rules worth knowing

- **Consent is an explicit opt-in.** "How may we contact you?" has no pre-selected answer, and
  "Email only" is a real choice (TCPA: calls and especially texts need prior express consent).
  Contact hours *may* default (Business hours), because that's a preference, not consent.
- **The sign-up link is public**, so the store id isn't a secret. Safety comes from the rules:
  only stores with `SignupEnabled`, only customer scopes, an existing membership is never changed,
  and a revoked customer can't re-admit themselves (403 `membership_inactive`).
- **Stable customer id:** `c-` + 12 hex of SHA-256(tenant, user). Double clicks and retries give
  the same id.
- **Google users** go through the same `ExternalIdentityKey`: brokered by Keycloak, the token's
  issuer is still Keycloak, so nothing downstream changes.

## 4. Set up Google (once)

See the redirect URI and steps in the session notes, or: Google Cloud Console → Google Auth
Platform → Clients → Web application, redirect URI
`http://localhost:8080/realms/builderpro/broker/google/endpoint`. Then:

```bash
dotnet user-secrets set "Parameters:google-client-id" "<id>" --project src/AppHost/BuilderPro.AppHost
dotnet user-secrets set "Parameters:google-client-secret" "<secret>" --project src/AppHost/BuilderPro.AppHost
```

Restart the AppHost: the login page shows **Continue with Google**. No secrets: no button, everything
else works.

## 5. Try it

1. Open http://localhost:4200/signup?store=acme, create an account. You land on "Welcome, Acme
   Home Improvement", and the catalog shows Acme's products.
2. Sign out, open `/signup?store=globex` and sign in with the same account: you're now a customer
   of both, and "Who am I" shows 409 until a tenant is chosen (tenant switching comes later).
3. Keycloak admin (http://localhost:8080, admin/admin) → Users → your user → Attributes: phone,
   consent, hours and address are stored there.

## 6. Not yet
- ~~Email verification and "Forgot password"~~: done in 1f (Mailpit locally, Zoho opt-in; docs/email.md).
- ~~Consent audit trail~~: done in 1f. Keycloak puts `contact_consent` / `contact_hours` in the access
  token; Identity appends to `identity.consent_events` at sign-up and on every change (the shell calls
  `POST /api/identity/consent/sync` once per sign-in), with the wording version from
  `Identity:ConsentWording`. Customers read their own history at `GET /api/identity/consent/history`.
  Bump `Identity:ConsentWording:Version` whenever the sign-up consent text changes.
- **Tenant switcher** for users who are customers of several stores.
