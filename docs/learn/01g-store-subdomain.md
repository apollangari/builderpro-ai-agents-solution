# LEARN 1g: a store per subdomain

**Time:** about 20 minutes · **Goal:** `acme.builderpro.everisys.com` looks and behaves like Acme's
site, without the address ever being the thing that grants access.

## 1. What the address does, and doesn't

| The subdomain... | ...does | ...does not |
|---|---|---|
| `acme.` | show Acme's name in the header; send `t-acme` as the `X-BuilderPro-Tenant` hint on every API call; hide the store picker; make `/signup` mean "sign up with Acme" | grant anything: every service still asks Identity whether the signed-in user is a member of `t-acme` |

A Globex customer who opens `acme.…` gets `tenant_not_permitted` / `no_membership` from the API, never
Acme's data. That's the same rule the store picker (1f-c) always had: the browser hints, the server decides.

## 2. The pieces

```
acme.localhost:4200 ── storeSlugFromHost() -> "acme"
      │  GET /api/identity/stores/acme   (anonymous; gateway route "identity-stores", rate-limited as public)
      ▼
{ id: "acme", name: "Acme Home Improvement", tenantId: "t-acme", signupEnabled: true }
      │  HostStore: SelectedStore.select("t-acme"), ThemeService.apply({ retailerName })
      ▼
every /api/ call carries X-BuilderPro-Tenant: t-acme  ->  TenantResolver checks the membership
```

- Rules for the slug (`web/src/app/core/tenant/host-store.ts`): one label in front of a known base
  domain (`localhost`, `builderpro.everisys.com`), lowercase letters/digits/hyphens; `www`, `app`,
  `api`, `admin` are never stores.
- Unknown store address: a banner "There's no store at this address", and no hint is sent.
- The public endpoint returns only what the storefront shows anyway.

## 3. Sign-in on a subdomain

Each origin keeps its own tokens, so `acme.localhost:4200` signs in separately, but Keycloak's own
session cookie (on `localhost:8080`) makes it one click. The realm lists each store origin in the
web client's redirect URIs and web origins (Keycloak can't wildcard a host). For production that list
comes from the stores table when the realm is provisioned (5a).

## 4. Production DNS (Cloudflare)

1. DNS: a proxied `CNAME *.builderpro` -> the web app's host (and `builderpro` itself).
2. TLS: Cloudflare's Universal SSL covers one level of wildcard under the zone (`*.everisys.com`);
   for `*.builderpro.everisys.com` add an Advanced Certificate (or Total TLS) for that wildcard.
3. Add `https://<store>.builderpro.everisys.com/*` to the web client's redirect URIs per store.

## 5. Try it locally

After an AppHost restart (the realm and gateway changed), open `http://acme.localhost:4200`, sign in
as alice: the header says Acme Home Improvement and there is no store picker. Then open
`http://globex.localhost:4200` as alice (an Acme-only customer): the API refuses, the address alone
grants nothing. `http://nope.localhost:4200` shows the "no store at this address" banner.
