# LEARN 1d: Identity service, memberships and claims normalisation

**Time:** about 90 minutes · **Goal:** understand why "who you are" (the token) and "what you
may access" (memberships) are different questions, and how the platform answers the second one
safely, whatever identity provider issued the token.

## 1. What changed in 1d

```text
Angular ──▶ Gateway ──/api/me──────────▶ Me API         (token view)
                    └─/api/identity/me─▶ Identity ──▶ identitydb (Postgres)   (platform view)
```

| Piece | Change |
|---|---|
| `src/Services/Identity/BuilderPro.Identity.Api` | **New.** Adapted from your `platform/identity`, trimmed to memberships |
| AppHost | Postgres (data volume + pgAdmin) with a database **per service** (`identitydb`); Identity service |
| `BuilderPro.Security` | `ExternalIdentityKey` (copied from your Shared lib), `UserContext` + `ClaimsNormalizer` |
| Gateway | Route `/api/identity/**` → identity |
| Keycloak realm | **Fixed user ids**; new scenario users `dana`, `erin`, `pat`; role `platform-admin` |
| Angular | **Who am I** shows the *token view* and the *platform view* side by side |

**What carried over from your Identity service:**
- The cross-IdP key: Entra `oid`, otherwise a hash of `iss`+`sub`.
- Revocation is a timestamp, never a delete, and every change is audited.
- It fails closed unless exactly one membership is active.
- A scope allow-list.
- Admin-only membership administration.
- No in-memory fallback at runtime.

**Dropped for now:** customer profiles, `/validate` + JWKS endpoints, and the failed-validation
lockout. They're good candidates for later increments, and the exercises show where they plug in.

## 2. Concepts

### Authentication ≠ tenancy
The identity provider tells you **who** someone is. It shouldn't decide **which retailer's data**
they can touch. Admins of the IdP can be careless, tokens live for minutes, and you may federate
customers' own IdPs later. So the platform keeps its own **memberships** (user → tenant, scopes,
expiry, revocation), and the token's tenant claim is only a **hint** that must match one.

### The tenant rule (`TenantResolver`, pure and unit-tested)
| Situation | Result |
|---|---|
| Exactly one active membership | ✅ that tenant |
| Token hints a tenant **and** there's an active membership there | ✅ that tenant |
| Token hints a tenant with **no** active membership | ⛔ 403 `tenant_not_permitted` |
| Several active memberships and no hint | ⚠️ 409 `multiple_active_memberships` (never "pick one") |
| No active membership | ⛔ 403 `no_membership` |

Expired or revoked memberships never count. Expiry is exclusive, so a membership is inactive *at*
its expiry time.

### One user key across identity providers
- **Entra:** `oid`. Entra's `sub` is *pairwise*: the same person has a different `sub` in every
  app registration, so keying on it would break the moment you add a second app.
- **Keycloak and others:** `sub` is unique only *per issuer*, so the key is
  `oidc_` + SHA-256(`iss` + newline + `sub`). The same `sub` from two issuers is two people.

### Claims normalisation
`ClaimsNormalizer` turns any provider's claims into **our** `UserContext` (user key, username,
email, roles, tenant hint). Code below the edge never reads raw claim names, which is what makes
"Keycloak in dev, Entra in prod, a customer's own IdP later" a configuration change. It's also
strict: two tenant claims, or a malformed one, means **no hint**, never "take the first".

### Database per service
One Postgres *server* locally, but `identitydb` belongs to the Identity service only. Other
services will get their own databases. No service reads another's tables; they call its API.

### Revocation and audit
Revoking sets `revoked_at` and `revoked_reason`; the row stays, and an audit row is written **in
the same SQL statement**. Revoking twice keeps the *first* time and reason (`COALESCE`).
Re-adding a user re-activates the same row (upsert), so the history stays in one place.

## 3. Code tour

| # | File | Notice |
|---|---|---|
| 1 | `BuilderPro.Security/ExternalIdentityKey.cs` | `oid` first; `iss`+`sub` hash otherwise; rejects two `oid`s |
| 2 | `BuilderPro.Security/UserContext.cs` | The normaliser; the strict tenant-hint rule |
| 3 | `Identity.Api/Domain/TenantResolver.cs` | The whole tenancy rule in about 30 pure lines |
| 4 | `Identity.Api/Domain/Membership.cs` | `IsActive`; the store interface |
| 5 | `Identity.Api/Infrastructure/PostgresMembershipStore.cs` | Plain SQL; data-modifying CTEs write row + audit atomically |
| 6 | `Identity.Api/IdentityEndpoints.cs` | Problem details with a stable `code`; admin policy; scope allow-list |
| 7 | `Identity.Api/DevSeed.cs` + `appsettings.Development.json` | Seeds memberships using the *same* key function as login; refuses Production |
| 8 | `src/AppHost/.../AppHost.cs` | `AddPostgres().AddDatabase("identitydb")`, `WithDataVolume`, `WithPgAdmin` |
| 9 | `tests/BuilderPro.Identity.Api.Tests/*` | Resolver unit tests + API tests with an in-memory store |
| 10 | `web/.../whoami.page.ts` | Two independent calls; a generic `Load<T>` state; `@let` in templates |

## 4. Debugging labs

> Run the AppHost. On the dashboard, `postgres`, `pgadmin`, `identity`, `keycloak`, `me-api`,
> `gateway` and `web` should all be running.

### Lab A: the five users (15 min)
Sign in as each user in turn (Sign out between them; password `Passw0rd!`) and open **Who am I**:

| User | Token view | Platform view |
|---|---|---|
| alice | tenant claim `t-acme` | ✅ `t-acme`, customer `c-alice` |
| carol | `t-globex` | ✅ `t-globex` |
| dana | *(none)* | ⚠️ 409, several tenants |
| erin | *(none)* | ⛔ 403, no membership |
| pat | *(none)*, role `platform-admin` | ⛔ 403 on `/me`, but can administer |

Then read `TenantResolverTests` and match each row to a test.

### Lab B: see the data (10 min)
1. Dashboard → `pgadmin` endpoint → server `postgres` → database `identitydb` → schema `identity`.
2. `SELECT tenant_id, user_key, customer_id, scopes, revoked_at FROM identity.memberships;`
3. Note that the user keys are `oidc_…` hashes, not Keycloak ids. Compute alice's yourself:
   ```bash
   printf 'http://localhost:8080/realms/builderpro\na11ce000-0000-4000-8000-000000000001' | sha256sum
   ```
   Add the `oidc_` prefix and compare.

### Lab C: revoke access live (15 min)
1. Sign in as **pat**, copy the bearer token from DevTools (any `/api/...` request), and:
   ```bash
   TOKEN=...   # pat's access token
   curl -s -H "Authorization: Bearer $TOKEN" "http://localhost:4200/api/identity/memberships?tenantId=t-acme" | jq
   ```
2. Copy alice's membership `id`, then revoke it:
   ```bash
   curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"reason":"lab C"}' http://localhost:4200/api/identity/memberships/<id>/revoke | jq
   ```
3. Sign in as **alice** and refresh **Who am I**. The token view still says `t-acme` (the token
   hasn't changed), but the platform view now says **403 `tenant_not_permitted`**.
   **The platform wins.** Revocation works immediately, even though alice's token is still valid.
4. Look at `identity.membership_audit`. Then restore alice: POST `/api/identity/memberships` with
   her `tenantId` and `userKey` (it re-activates the same row).

### Lab D: step through tenant resolution (10 min)
Set breakpoints in `IdentityEndpoints.GetMeAsync` and `TenantResolver.Resolve`. Sign in as
**dana** and open **Who am I**. Inspect `user.TenantHint` (null), then the `active` array (two
memberships), and watch it take the `Ambiguous` branch.

### Lab E: follow it in one trace (5 min)
Dashboard → **Traces** → the newest `GET /api/identity/me`. You'll see gateway → identity → a
**PostgreSQL span** with the SQL (from the Aspire Npgsql integration).

### Lab F: Entra-shaped tokens without Entra (5 min)
Read `ClaimsNormalizerTests.Entra_token_is_keyed_by_oid_not_by_the_pairwise_sub`. Why does
`tid` *not* become the tenant? Because `tid` is the customer's **Azure directory**, not our
**retailer tenant**: mapping one to the other is a business decision for the membership store.

## 5. Exercises

1. **Tenant picker for dana.** When `/api/identity/me` returns 409, show a picker listing her
   tenants (new endpoint `GET /api/identity/me/tenants`). Send the choice as a request header
   that the resolver treats like a hint. Write the resolver tests first: what if she picks a
   tenant she isn't in?
2. **Admin UI.** A `/admin/memberships` page for `platform-admin` only: list, add, revoke. Guard
   the route by role in Angular, *and* remember the API is the real check.
3. **Membership expiry.** Add a membership for erin that expires in 2 minutes. Watch her lose
   access without anyone revoking. Which test proves expiry is exclusive?
4. **Customer profile (from your original service).** Port `CustomerProfile` with its validation
   rules, as schema version 2 in `identity.schema_history`. Don't edit version 1.
5. **Hint mismatch, on purpose.** In Keycloak admin, change alice's `tenant_id` attribute to
   `t-globex`. Predict both views before you check.

## 6. Review questions

1. *Why not just trust the `tenant_id` claim?*
   > Tokens are issued by a system with its own admins and lifecycle. Access must be revocable
   > instantly and auditable on the platform side. The claim is a hint that must match a
   > membership.
2. *Why fail closed on multiple memberships instead of picking the most recent one?*
   > Silently picking a tenant can show one retailer's data to someone acting for another. An
   > explicit choice is safer and auditable.
3. *Why key users by `oid` for Entra?*
   > Entra's `sub` is pairwise per application. The same human would look like different users
   > to different apps.
4. *Why is the audit row written in the same SQL statement?*
   > Atomicity: either the change and its audit both happen, or neither does. There's no window
   > where access changed without a record.
5. *Why is there no in-memory store fallback at runtime?*
   > A silent fallback loses all memberships on restart. Your original service learned this the
   > hard way. Failing fast at startup is visible; data loss isn't.

## 7. Further reading

- Microsoft identity platform: ID token claims (`oid`, `sub`, `tid`): https://learn.microsoft.com/entra/identity-platform/id-token-claims-reference
- Aspire PostgreSQL integration: https://aspire.dev
- PostgreSQL data-modifying statements in `WITH`: https://www.postgresql.org/docs/current/queries-with.html
- RFC 9457 Problem Details: https://www.rfc-editor.org/rfc/rfc9457
