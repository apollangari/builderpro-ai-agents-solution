# Local Keycloak

`realms/builderpro-realm.json` is imported when the Keycloak container starts (`--import-realm`).
Admin console: http://localhost:8080 (admin / admin).

All passwords: `Passw0rd!`. **Dev only.** These are throwaway credentials for a local container.
Each seeded user has a complete fictional profile (555-01xx phone, contact consent, Springfield IL address),
so the required sign-up fields never force an "update profile" step for them.

| User | Fixed id | Token `tenant_id` | Memberships (Identity service, seeded in Development) | What you'll see on "Who am I" |
|---|---|---|---|---|
| alice | `a11ce000-…-0001` | t-acme | t-acme (customer c-alice) | ✅ t-acme |
| bob | `b0b00000-…-0002` | t-acme | t-acme (staff) | ✅ t-acme |
| carol | `ca201000-…-0003` | t-globex | t-globex (customer c-carol) | ✅ t-globex |
| dana | `da7a0000-…-0004` | — | t-acme **and** t-globex | ⚠️ 409 multiple_active_memberships |
| erin | `e1110000-…-0005` | — | none | ⛔ 403 no_membership |
| pat | `9a700000-…-0006` | — | none (role **platform-admin**) | ⛔ 403 on /me, but can administer memberships |

User ids are **fixed** in the realm file so the Identity service can seed memberships for them
(a membership is keyed by issuer + subject; see `ExternalIdentityKey`).

## Sign-up and Google (1e)

- **Create account** is on the login page; a store's link opens it directly: http://localhost:4200/signup?store=acme
- Sign-up asks for name, email, password, **phone**, **contact consent** (calls / texts / both / email only,
  no default), **contact hours** (default Business hours) and the **project address**. Rules live in the realm's
  user profile (`components` in the realm file).
- **Continue with Google** appears only when the AppHost has `Parameters:google-client-id` and
  `Parameters:google-client-secret` in user secrets (see docs/learn/01e-signup-login.md).
- Theme: `themes/builderpro` (Navy & Sand CSS on Keycloak's `keycloak.v2` templates), bind-mounted by the AppHost.
- **Email:** verification is ON. Emails go to **Mailpit** (http://localhost:8025) unless the `smtp-*` user secrets
  switch on real sending (Zoho for builderpro.everisys.com). See [docs/email.md](../../docs/email.md).
  The seeded test users are already verified.

Changed the realm file? The container has no persistent volume, so it re-imports on every start.
Stop and start the AppHost.
