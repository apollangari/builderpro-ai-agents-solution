# LEARN 2a: Catalog service (products per tenant)

**Time:** about 45 minutes · **Goal:** add a second business service that is tenant-safe by
construction, and see one service call another with the caller's own identity.

## 1. What changed

```text
browser ──► gateway ──► catalog ──► identity      (GET /api/identity/me, caller's token)
                           │
                           └──► Postgres: catalogdb  (products, keyed by tenant_id + sku)
```

| File | What it does |
|---|---|
| `src/Services/Catalog/BuilderPro.Catalog.Api/` | The new service |
| `Domain/Product.cs` | `Product` record and `IProductStore`. Every store method takes a `tenantId` |
| `Tenancy/TenantLookup.cs` | Asks Identity "which tenant is this caller in?" |
| `CatalogEndpoints.cs` | `GET /api/catalog/products`, `GET /api/catalog/products/{sku}` |
| `AppHost.cs` | `catalogdb` database + `catalog` project, referenced by the gateway |
| `Gateway/appsettings.json` | New `catalog` route and cluster |

## 2. Concepts

### The tenant is resolved in ONE place
Catalog does **not** read the `tenant_id` claim. LEARN 1d established that memberships are the
source of truth and a claim is only a hint. Rather than copy `TenantResolver` into every
service (and let the copies drift), Catalog calls Identity's `/api/identity/me`, **forwarding
the caller's own bearer token**. Identity validates it, applies the rule, and answers with the
tenant or a refusal.

Trade-off: one extra internal call per request. That's cheap locally and is the obvious place
to add a short cache later. Correctness first.

### Fail closed
| Identity answers | Catalog returns |
|---|---|
| 200 with a valid `tenantId` | the products |
| 401 / 403 / 409 | the **same** status and `code` (`no_membership`, `multiple_active_memberships`, ...) |
| 5xx, timeout, garbage, unreachable | **503** `tenant_lookup_unavailable` |

There is no branch that shows "all products" or "trusts the claim" when Identity is down.

### Isolation by construction
- The primary key is `(tenant_id, sku)`. Two retailers can use the same SKU (the dev seed does
  this on purpose with `PAINT-INT-WHT-1G`).
- Every SQL statement has `tenant_id = $1`. There is no "list all" method to misuse.
- Another tenant's SKU returns a plain **404**, indistinguishable from a missing one, so the API
  doesn't even confirm it exists.
- `TenantId` is not in the response DTO, so clients are never tempted to send it back.

### Price is data, not a calculation
`ListPrice` is a `decimal` the retailer maintains, with an ISO currency. Totals, discounts and
rounding come in 2c as domain code. That's the "agents decide; the domain enforces" rule: an
agent will read prices from this API and never compute one.

## 3. Try it

1. Run the AppHost. In the dashboard, `catalog` waits for `catalogdb` and `identity`.
2. Open http://localhost:4200/catalog and sign in as `alice` (t-acme): three products.
   To call the API directly, copy the bearer token from any `/api/...` request in the dev tools
   **Network** tab:
   ```bash
   curl -s http://localhost:4200/api/catalog/products -H "Authorization: Bearer <paste token>"
   ```
3. Sign in as `carol` (t-globex): same SKU, different name and price.
4. Sign in as `dana` (two tenants): **409** `multiple_active_memberships`.
5. **Traces:** open one catalog request in the dashboard. You'll see gateway → catalog →
   identity → Postgres as one trace.

## 4. Debugging labs

1. **Stop `identity`** in the dashboard and call the catalog again. Expect **503**, not data.
   Start it again; the next call works (the resilience handler retries transient failures).
2. **Break isolation on purpose:** remove `tenant_id = $1 AND` from `ListActiveAsync`. Which
   test catches it? (`A_member_sees_only_their_own_tenants_active_products`.) Put it back.
3. **Revoke a membership** through the Identity admin API, then call the catalog with the same,
   still-valid token. Access stops immediately: the token didn't change, the membership did.

4. **Restart the AppHost while signed in**, then reload `/catalog`. Every API answers 401 with
   `error_description="The signature key was not found"`. Keycloak generated new signing keys on
   start, so the token your browser kept was signed by keys that no longer exist. Sign out and in
   again. (In production the IdP keeps and rotates its keys, so this doesn't happen.)

## 5. Review questions

1. *Why forward the user's token instead of using a service-to-service credential?*
   > Identity must answer "who is THIS caller", which only the caller's token proves. A service
   > credential would let Catalog ask about anyone.
2. *Why 404 instead of 403 for another tenant's SKU?*
   > 403 confirms the SKU exists somewhere. 404 leaks nothing.
3. *Why not copy `TenantResolver` into Catalog?*
   > Two copies of a security rule drift. One owner, one rule, one set of tests.

## 6. Not yet

- No admin API for products (the dev seed fills `catalogdb`). Comes with the retailer back office.
- No cache on the tenant lookup.
