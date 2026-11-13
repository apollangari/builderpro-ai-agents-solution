# LEARN 2n-b..e: the guided quote

**Time:** about 45 minutes · **Goal:** a step-by-step quote (measure, options, protection, quote)
that works for every project type, where the browser collects answers and the server does every
calculation.

## 1. One flow, data per category

```
ZIP -> project type -> product -> Measure -> Options -> Protection -> Quote -> Save
                                   │          │          │
              category.Measure ────┘          └──────────┴── the store's option steps (catalog v4)
```

| Category | Measure form | Product bought by | Example option steps (dev seed) |
|---|---|---|---|
| Countertops | `countertop`: sections, island, backsplash, layout | `sqft` | edge (per ft of edge), removal (per sq ft), sinks (counts), cut-outs (4 faucet holes included), protection plan |
| Roofing | `roof`: footprint, pitch, waste, eaves + rakes | `bundles` | tear-off (per square), underlayment (rolls), drip edge (pieces) |
| Flooring | `area`: rooms | `sqft` (+10 % waste) | removal (per sq ft of floor), pad |
| Everything else | `count` | `count` | sheds: foundation |

New categories need no new code, just option steps in the catalog.

## 2. Who calculates what

| Step | Where | Rule |
|---|---|---|
| ft + in -> inches | browser | input conversion only |
| areas, edge length, bundles | `Quotes.Domain/Measurements.cs` | rounded **up** (you can't install 0.3 of a square foot short) |
| which lines, which quantities | `Quotes.Domain/GuidedQuote.cs` | basis per option step; included counts; minimum as its **own line** |
| money | `QuotePricer` | same rules as every quote |
| prices | catalog, for the ZIP's zone | products **and** options; no price = not offered there |

`POST /api/quotes/guided/preview` runs all of it on every change; the page only renders the answer.

## 3. Option steps

```json
{ "Id": "ct-edge", "Label": "Edge style", "Selection": "single", "Basis": "edgeFt", "Required": true,
  "Options": [ { "Sku": "EDGE-OGEE", "Name": "Ogee edge", "Unit": "ft", "ListPrice": 18.00 } ] }
```

- **Selection** `single` (one option, or "none" when `NoneLabel` is set) or `quantity` (counts per option).
- **Basis** for single steps: a measured value (`edgeFt`, `sqft`, `squares`, ...) or `fixed` (1, e.g. a plan).
- **Required**: the customer must answer, even if the answer is "none". The preview lists open steps in
  `missing`; saving with open steps is `400 incomplete_quote`.
- **AppliesAttribute/Values**: e.g. protection plans only for `material` = quartz, granite, marble, solid surface.
- **Step** `protection` puts it on the protection page; it is priced exactly like an option.

## 4. What a saved quote keeps (quotes schema v2)

The priced lines (as before) **plus** the ZIP, each line's group (`product`, `minimum`, `options`,
`protection`) and the request as sent: measurements and answers, never prices. Re-pricing a quote later
is "send the same request again"; comparing the totals shows what changed.

## 5. Try it

Restart the AppHost, sign in as alice, **Start a project** -> 75078 -> Countertops -> a quartz top.
Try 4 ft x 25 in (watch the minimum line appear), add an island (the edge length grows), switch to
ZIP 60601 (every price, options included, is 12 % higher).

## 6. Not yet

- The cabinets question ("are cabinets part of the project?") and plumbing disconnect.
- Percentage-priced protection plans (flat per zone only).
- The concierge (3b) filling these same steps.
