# Roadmap

Small deliverables, one PR each, merged into `development` with CI green. Each one runs end to
end, ships its tests, and (for increments) a `docs/learn/` guide. The plan below is a direction,
not a contract: anything past the next two or three rows will be re-cut as we learn.

> **Order follows the one rule.** *Agents decide; the domain enforces.* So the domain services an
> agent will call (catalog, pricing) exist, with tests, **before** any agent does.

## Done

| # | Deliverable |
|---|---|
| 1a | Walking skeleton: Aspire + Angular → API |
| 1b | Keycloak login, API validates the token |
| 1c | YARP gateway in front |
| 1d | Identity service, claims normalisation, memberships |
| 1x | Aspire 13.6.5 + ServiceDiscovery 11.0.0 (security advisories, restore fix) |
| 0a | This roadmap; README stack version |
| 0b | `ASPIRE010` resolved: keep the NuGet-pinned dashboard, warning suppressed with a reason |
| 2a | Catalog service: tenant-scoped products, tenant resolved by Identity ([LEARN](learn/02a-catalog-service.md)) |
| 2x | WhoAmI page on `httpResource` (signal-first Angular) |
| 2y | Design system (tokens, light/dark) and refreshed shell, home and WhoAmI pages |
| 2b | Catalog page: tenant products on `httpResource`, live search, fail-closed errors |
| 2c | Pricing as pure domain code: `Money`, one rounding rule, `QuotePricer` ([LEARN](learn/02c-pricing-domain.md)) |
| 2d | Quote API: catalog prices + `QuotePricer`, scopes, customer visibility ([LEARN](learn/02d-quote-api.md)) |
| 2y' | Navy & Sand theme (Option A), `ThemeService` white-label accent |
| 2e | Dapper in every store (SQL unchanged); store tests against real Postgres (Testcontainers); roofing products in the dev seed |
| 2f | Redis for hot reads: tenant lookup + product lists, invalidated by the data owner ([LEARN](learn/02f-redis-hot-reads.md)) |
| 3a | Read-only product assistant (`/assistant`): Microsoft Agent Framework agent with catalog/quote tools (categories, search, roof estimator, `price_products`); a server-side price guard blocks any amount no tool returned; Foundry gpt-4o or any OpenAI-compatible local LLM, chosen in user secrets ([LEARN](learn/03a-product-assistant.md)) |
| 2m | Quote builder: Add to quote from the picker, `/quote` with editable quantities and a live server preview (`POST /api/quotes/preview`, same pricing path as create), roof calculator adding shingles/underlayment/drip edge/labour found by attributes, save, `/quotes` (My quotes) |
| 2g | Roof estimator (domain): footprint + pitch + waste + edges -> pitch factor, squares, bundles, underlayment rolls, drip edge (materials rounded up); `POST /api/quotes/estimators/roof`, quantities only |
| 2l-b | "My photo": upload a room photo (stays in the browser, 15 MB max), drag 4 corners (mouse, touch or arrow keys) and the product is drawn in perspective (homography + triangle subdivision on canvas; paint as a multiply colour layer) |
| 2l-a | Visualizer default scenes for every product type: kitchen (countertops, cabinets), house roof, backyard (sheds, decking, fencing), room floor, room wall (paint colour), house front (windows, doors); "See it in a room" from the picker; switch options in place |
| 2k | Compare: pick 2-4 products of one category; side-by-side attributes; estimates at the category's typical size from `POST /api/quotes/estimates` (catalog price + QuotePricer, tax incl., nothing stored); lowest marked |
| 2j | Product picker: category tabs with counts (only categories the store sells), attribute filters per category (only values that exist), image cards with attribute badges; pure picker functions + signals |
| 2i | Sample images: 25 generated SVGs in `web/public/products` (stone, shingles, sheds, planks, cabinets, paint, windows, fences) from `web/scripts/generate-product-images.mjs`; shown on catalog cards; a test fails if a seeded image file is missing |
| 2h | Product categories + attributes: 11 platform categories (Domain/Categories.cs) with typed attribute definitions and a default scene each; schema v2 (`category`, `attributes jsonb`, `image_url`); `GET /api/catalog/categories`, `?category=`; 29 seeded products validated at startup and by a test |
| 1f-c | Store picker: `X-BuilderPro-Tenant` hint (validated against memberships), forwarded by Catalog/Quotes, part of the cache key; picker in the header for multi-store customers; seeded users get complete profiles |
| 1f-b | Consent audit trail: token claims -> append-only `identity.consent_events` (choice, hours, wording version, source, time); own history API |
| 1f-a | Email: Mailpit locally (verification on, password reset), Zoho opt-in via user secrets ([docs/email.md](email.md)) |
| 1e | Sign-up via store link, Navy & Sand Keycloak login, Continue with Google (when secrets set), address / phone / contact consent / contact hours on sign-up ([LEARN](learn/01e-signup-login.md)) |

## Next

| # | Deliverable | Done when |
|---|---|---|
| 2n-a | **Guided quote: confirm ZIP, then category + product.** `/quote/new` asks "Is this project at ZIP 75201?" using the ZIP from the sign-up address; "No" asks for the project's ZIP. Stores keep service areas server-side as **price zones** (sets of ZIP codes); each product has a price per zone and is only sold in zones that have a price. Then category, then the picker (filters, price per unit **for that zone**, all-in "material + fabrication + install" when the store says so, sale price shown against the regular price) | ZIP lists are never sent to the browser; an unserved ZIP gets "we don't serve that area yet"; the same product can show different prices in two ZIPs; a product with no price in the zone never appears or prices |
| 2n-b | **Measurements per category** (one form per category, server-side estimators, quantities only). Countertops: room, existing top to remove (none / stone-tile / laminate-solid surface), "are cabinets part of the project", countertop sections (width x depth, standard depth 25"), island/peninsula sections, optional backsplash (width x height), layout (L, L + island, U, U + island) -> sq ft per area, total, finished-edge length. Store **minimum charge** (e.g. 25 sq ft) applied by the pricer. Roofs: existing calculator. Flooring/decking: rooms. Fencing: run + height. Sheds/windows/doors: count or size | The browser never computes a quantity; each estimator has domain tests; the minimum shows as its own line |
| 2n-c | **Add-ons / options per category**, as ordered steps the store configures, each priced from that zone's prices: countertop edge style (picture, price per linear inch/ft of finished edge), sink (new sink from a list with quantities / bring your own / no sink), cutouts (faucet holes: N included then each; cooktop: 1 included then each), removal of the old top, plumbing disconnect/reconnect. Required/optional, single/multi, "included" quantities | Options, included quantities and their prices come from the server; invalid combinations are rejected by the API |
| 2n-d | **Protection plans**: shown only when the chosen product's material is covered; tiers like "5-year" / "10-year" with a flat price per zone (or % of product + add-ons), plus coverage text; "No plan" is an explicit, required choice | Plan price comes from the pricer; a plan never appears for a product it doesn't cover |
| 2n-e | **Quote review + save**: no contact form (the customer is signed in; address, phone and consent come from the profile). Grouped quote: product, measurements summary, add-ons, minimum-charge adjustment, protection, tax, total; saved as a draft with the ZIP, measurements and choices; My quotes shows it | The saved quote is reproducible from its stored inputs; totals match the preview |
| 3b | **Agent drafts a quote**: agent proposes lines, the Quote API prices them, a human approves | The agent cannot approve its own quote |
| 3c | Expose catalog/quote tools over **MCP** | An MCP client can list and call the tools |
| 4a | Python **LangGraph** agent talking to the .NET agent over **A2A** | One cross-agent conversation, traced end to end in the dashboard |
| 5a | Azure: Terraform for one environment, deploy `development` | `dev` environment reachable, Entra login |

## Decisions

- **1e onboarding (2026-11-13): a sign-up link per store.** `/signup?store=acme` sends the visitor to Create account
  (or Continue with Google) and, on first sign-in, the Identity service creates a **customer** membership for that
  retailer with default customer scopes (`quotes.read`, `quotes.write`). Rules: only active stores with sign-up
  enabled accept it; the store id is carried through the login round trip in signed state, never trusted from a
  later request; an existing membership is never changed by a sign-up link; staff roles are never granted this way.
  Without a store, a new user sees "ask your retailer for a sign-up link" instead of an error.

- **3a LLM provider (2026-11-13): Microsoft Foundry, gpt-4o deployment.** Key, endpoint and deployment live in
  AppHost user secrets locally (Key Vault in Azure). For development, any OpenAI-compatible server (a free local
  LLM) can be used instead by setting `assistant-provider=local`; never with real customer data.

- **Regular customer flow (2026-11-13, from the owner):** ZIP code -> product category -> only products for
  that ZIP and category -> measurements -> add-ons/options (e.g. countertop edge, faucet holes, sinks) ->
  protection plan -> quote. Built as the guided quote 2n-a..e; the assistant (3b) drafts quotes through the
  same steps, asking for the ZIP first. Defaults until the owner says otherwise:
  - The ZIP starts as the one from the sign-up address; the customer confirms it or enters the project's
    ZIP. It is checked against the store's service areas (never shown) and does not pick a store (a
    customer is still signed up through one store's link). A new ZIP is kept on the quote, not the profile.
  - Prices vary by ZIP: a store groups its ZIPs into price zones and prices each product, add-on and plan
    per zone (owner, 2026-11-13). Tax stays per store for now.
  - Add-ons and protection plans are catalog items the store maintains, never hard-coded prices.
  - The existing quote builder (2m) stays for staff until 2n-e replaces it for customers.
  - Every project type uses the same wizard (ZIP -> product -> measurements -> options -> protection ->
    quote); each category supplies its measurement form and option steps as data. Countertops come first,
    modelled on a large home-improvement retailer's public countertop wizard (flow only, no content copied).

## Open questions

- **Foundry quota:** the gpt-4o deployment answers one call and then rate-limits (HTTP 429) the follow-up call
  after a tool runs, so the assistant reports "busy". Raise the deployment's tokens-per-minute limit in the
  Foundry portal (Deployments → gpt-4o → Edit), or use a local OpenAI-compatible LLM for development.

## Conventions

- **Angular is signal-first.** Server data comes through `httpResource` (value / isLoading /
  error / reload as signals). RxJS only where angular-auth-oidc-client requires it (its config
  loader and `logoff()`). This applies to every new page, including the agents' chat UI.

## How a deliverable is sized

- Fits in one sitting (roughly 1–2 hours of reading + running it).
- One PR, one squash commit, conventional title.
- Leaves `development` releasable: build, tests and CI all green.
