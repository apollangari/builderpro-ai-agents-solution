# LEARN 2d: Quote API

**Time:** about 40 minutes · **Goal:** see three services cooperate on one request, each
enforcing its own rule, with the money computed in exactly one place.

## 1. One request, end to end

```text
POST /api/quotes { lines: [{ sku, quantity }] }
  gateway ──► quotes ──► identity   tenant, customer, scopes      (caller's token)
                    ├──► catalog    list prices of THAT tenant    (caller's token)
                    ├──► QuotePricer (2c, pure)  totals, tax
                    └──► Postgres quotesdb  quote + lines, one transaction
```

The request carries **SKUs and quantities only**. Unit prices come from the catalog, totals and
tax from `QuotePricer`, the tenant from Identity. A `unitPrice` or `total` in the body is ignored
(there's a test for that).

## 2. Rules and where they live

| Rule | Where |
|---|---|
| Which tenant / customer / scopes | Identity (via `ITenantLookup`, now shared in `BuilderPro.Security`) |
| Which products and prices exist | Catalog (its own tenant isolation applies) |
| Totals, rounding, tax, validation | `QuotePricer` (2c) |
| Tax rate per tenant | `Quotes:TaxRates` config (t-acme 8.25 %, t-globex 7 %) |
| `quotes.write` to create, `quotes.read` to read | Quotes endpoints |
| Customers see their own quotes; staff see the tenant's | `IQuoteStore` filters (`customer_id`) |

## 3. Concepts

- **Snapshot, not a view.** A stored quote keeps the prices and totals it was priced with. If the
  catalog price changes tomorrow, yesterday's quote doesn't.
- **Fail closed.** Catalog unreachable → `503 catalog_unavailable`, nothing stored. We never price
  from a cache, a guess or the request.
- **Errors are data.** `400 invalid_quote` returns every problem with a `code` and `line`:
  `unknown_sku`, `invalid_quantity`, `duplicate_sku`, ... An agent (3b) can fix all of them in one go.
- **Shared tenant lookup.** The second service that needed "which tenant?" is the moment to move
  the code into the building block, not the first.

## 4. Try it

Run the AppHost, sign in as `alice`, copy the bearer token (Network tab), then:

```bash
curl -s http://localhost:4200/api/quotes -H "Authorization: Bearer $T" -H "Content-Type: application/json" \
  -d '{"lines":[{"sku":"PAINT-INT-WHT-1G","quantity":3},{"sku":"LABOR-INSTALL-HR","quantity":2.5}]}'
curl -s http://localhost:4200/api/quotes -H "Authorization: Bearer $T"
```

Then sign in as `bob` (acme staff, `quotes.read` only): he sees alice's quote but gets
`403 missing_scope` when creating one. `carol` (globex) gets 404 for alice's quote id.

## 5. Labs

1. Stop `catalog` in the dashboard and create a quote: 503, and `quotesdb` has no new row.
2. Send `DECK-BOARD-CED-8` as alice: `unknown_sku`, because it's globex's product.
3. Change t-acme's tax rate in `appsettings.json`, restart `quotes`: new quotes use it, old ones
   keep theirs.

## 6. Not yet
- Approval / status changes (3b, with a human in the loop), idempotency keys, a quotes UI.
