# Local Keycloak

`realms/builderpro-realm.json` is imported when the Keycloak container starts (`--import-realm`).
Admin console: http://localhost:8080 (admin / admin).

All passwords: `Passw0rd!`. **Dev only.** These are throwaway credentials for a local container.

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

Changed the realm file? The container has no persistent volume, so it re-imports on every start.
Stop and start the AppHost.
