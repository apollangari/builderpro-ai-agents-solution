# Roadmap

Small deliverables, one PR each, merged into `development` with CI green. Each one runs end to
end, ships its tests, and (for increments) a `docs/learn/` guide. The plan below is a direction,
not a contract: anything past the next two or three rows will be re-cut as we learn.

> **Order follows the one rule.** *Agents decide; the domain enforces.* So the domain services an
> agent will call (catalog, pricing) exist, with tests, **before** any agent does.

## Done

| # | Deliverable |
|---|---|
| 3e | A person can take over: chats kept in the Assistant's own database; staff (support.chat) list, take over (concierge paused, no model call), reply by name, hand back; the customer always sees who answers. Store desk /desk: chats + work queue (visits to measure, installs to move on, calls asking for a person) ([LEARN](learn/03e-staff-takeover.md)) |
| 4b | AI phone scheduling (store option phone-scheduling): separate automated-call consent with wording version (append-only); calls only inside contact hours and 9-20; offers up to 3 visit times, 0 for a person, 9 to stop (opt-out cancels every call); never books (confirm link -> customer confirms); dispatcher + telephony seam (simulated; ACS/Twilio later); signed provider callback ([LEARN](learn/04b-phone-scheduling.md)) |
| 4a | Python **LangGraph** scheduling agent over **A2A 1.0** (agent card, JSON-RPC SendMessage): proposes up to 3 measure-visit times within the customer's contact hours, never books; the concierge calls it (propose_visit_times); on-behalf-of at every hop (Assistant -> builderpro-scheduling -> builderpro-api), no token passthrough; Aspire uvicorn app with OTLP traces; CI pytest job ([LEARN](learn/04a-a2a-scheduling-agent.md)) |
| 3c | Catalog/quote tools over **MCP** (Streamable HTTP, read and price only) with **on-behalf-of** auth: the Assistant accepts tokens for itself and calls the platform with an RFC 8693 / Entra-OBO exchanged token for the same user (no passthrough); RFC 9728 discovery; realm clients builderpro-assistant and builderpro-mcp ([LEARN](learn/03c-mcp.md)) |
| 6d | Stripe payments + payment state machine (owner): created -> processing -> succeeded / failed / unknown, reconciliation by status lookup or signed webhook; every move recorded (payment_events); Stripe Checkout with idempotent sessions, signed-webhook verification (replay window, amount check), background reconciler, test keys only outside Production; simulated provider gains Decline ([LEARN](learn/06d-stripe-payments.md)) |
| 6c | Install tracking: staff move scheduled -> fabricating -> installing -> done (one step at a time; date can move until fabrication); each step records order updates only on the customer's consented channels (latest consent from Identity, email-only when unknown) in an outbox; workspace step strip ([LEARN](learn/06c-install-tracking.md)) |
| 6b | Measure visit: store visit times (zone, days, per-slot capacity), book/move by the customer or scheduling staff; staff record measured sizes re-priced at the HELD unit prices into a new numbered quote; same total -> install, changed total -> customer confirms (never staff or an agent) ([LEARN](learn/06b-measure-visit.md)) |
| 1i | Salesperson login: scopes `sales` / `sales.report`; customer search limited to the store's own customers; projects started on a customer's behalf ("prepared by"), editable only by that salesperson or the customer; Accept and deposit stay customer-only; commission report from paid deposits at the store's rate; Sales page. Invite-by-link not yet ([LEARN](learn/01i-salesperson.md)) |
| 6a | Deposit holds the price: Accept no longer holds it; the customer pays a 5 % deposit (DepositPolicy on the stored total, per-store percent) through a test-mode payment page; paid = price held, project to the measure visit, answers locked; customer only (staff and agents refused) ([LEARN](learn/06a-deposit.md)) |
| 1h | Packages per store: `guided` (always), `help-chat` (answers only), `concierge` (acts on the project), `phone-scheduling` (4b); set per store in Identity, carried by the tenant lookup, enforced in the Assistant (403 `feature_not_included`, help-only tool set), hidden in the web (Home, catalog chat, project chat); Acme full, Globex guided + help ([LEARN](learn/01h-store-packages.md)) |
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
| 2n-f | Project workspace `/projects/{id}`: projects on the server (answers + stage, re-priced on read), stage rail Discover -> Configure -> Quote -> Deposit, concierge column working on the project (`save_answers`, `issue_quote`), layout sketch (estimated), selection, steps saved as you go, numbered quotes Q-1001.. valid 14 days, **Accept quote** customer-only (refuses staff, outdated, expired, and any agent via `X-BuilderPro-Agent`); My projects + account menu behind the avatar ([LEARN](learn/02n-f-project-workspace.md)) |
| 1g | Store from the subdomain: `acme.localhost` / `acme.builderpro.everisys.com` shows the store's name, sends its tenant as the `X-BuilderPro-Tenant` hint (server still checks membership), hides the store picker, and `/signup` needs no `?store=`; public `GET /api/identity/stores/{id}` (anonymous gateway route, public rate limit); unknown store banner; Keycloak redirect URIs per store origin ([LEARN](learn/01g-store-subdomain.md)) |
| 3b | **Concierge** on `/assistant` (nav "Concierge"): chat beside a live draft quote. Agent Framework agent with 5 tools (check_zip, list_project_types, find_products, get_project_steps, price_draft) walks ZIP -> type -> product -> measurements (feet + inches converted in code) -> options -> protection, pricing a draft through `POST /api/quotes/guided/preview`; the page keeps the draft and sends it back each turn (no server chat state); only the customer's "Save quote" posts it; price guard as in 3a ([LEARN](learn/03b-concierge.md)) |
| 2n-b..e | Guided quote, steps 4-7 on `/quote/new`: measurements per category (countertop sections/island/backsplash/layout -> sq ft + finished edge; roof calculator; flooring rooms + 10 % waste; counts), the store's option steps (catalog v4: edge per ft, removal per sq ft, sinks and cut-outs with included counts, roofing tear-off/underlayment/drip edge), protection plans only for covered materials with an explicit "No plan", a visible minimum-charge line (25 sq ft countertops), grouped quote via `POST /api/quotes/guided/preview` on every change, save with ZIP + request + line groups (quotes v2), My quotes shows title and ZIP; project-type tiles with pictures ([LEARN](learn/02n-guided-quote.md)) |
| 2n-a | Guided quote, step 1 (`/quote/new`, "Start a project"): confirm the sign-up ZIP (`postal_code` claim) or enter the project's ZIP; `GET /api/catalog/service-area` (yes/no only); stores' **price zones** (ZIP sets, schema v3) with a price per product per zone; `/api/catalog/products?zip=` lists only what's sold there at that zone's price; quotes and estimates take `zip` and price the same way (`zip_not_served` 422, `invalid_zip` 400); project types shown only when sold in the ZIP ([LEARN](learn/02n-a-zip-pricing.md)) |
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
| 7a | **Retailer admin** (store role `store.admin`, owner 2026-11-14; reference: old app RetailerAdminPage / CatalogPublishEditor): the store's catalog as a DRAFT -> PUBLISH flow (product types -> products -> option steps -> options, area prices; validated as a whole, versioned, the live version unchanged until publish); store settings within its package: chat mode (concierge, regular help chat, or both), chat on the left or right, sign-up open/closed, branding (name, website colours applied across the app, logo, support contacts); orders and bookings for the store | A retailer admin publishes a new product with its options and customers see it only after publishing; only their own store; customers and other staff can't |
| 7b | **Platform (portal) admin** (global role `platform-admin`, owner 2026-11-14; reference: old app PlatformAdminPage): all retailers and the tool: create / edit stores (tenant, slug, name, package features, sign-up), memberships and staff roles per store (incl. retailer admins), catalog publishing oversight, audit trail, service health | Only platform admins; every change audited; a store's data stays separated |
| 3d | **ON HOLD (owner, 2026-11-14).** **Estimate from photos**: the customer attaches kitchen photos; a vision model proposes layout (L / U / island), approximate sq ft and cut-outs, shown as "Estimated from photos" with "final numbers are confirmed at the measurement visit" | Estimates are labelled everywhere they appear (layout, quote lines) and replaced by measured values after the visit |
| 5a | Azure: Terraform for one environment, deploy `development` | `dev` environment reachable, Entra login |

## Target experience (owner's mockup, 2026-11-13)

One project page per job, store-branded. Left: the **Concierge** chat ("answers instantly, a person can
take over anytime"). Right: a stage rail (Discover, Configure, Quote, Deposit, Measure visit, Install) and
panels for the current stage: layout (estimated from photos until measured), the selection, and the quote
(numbered, "priced by the store's pricing engine", valid N days, Accept quote / Ask about this quote).
Principle shown on the page: *the concierge can prepare things, but never accepts or pays for you.* Built
as 2n (guided steps) -> 2n-f (workspace) -> 3b (concierge) -> 3d (photos) -> 3e (takeover) -> 6a-c.

## Final phase: gaps (after all development work, owner 2026-11-13)
- **Keycloak keeps no data between restarts** (found 2026-11-14): runtime accounts (Google sign-in, sign-ups) are lost on every app start and get a new id (so their memberships no longer match). Fix: persistent Keycloak storage (volume or Postgres) and apply realm-file changes with keycloak-config-cli instead of a one-time import.

Fixed last, together, once the roadmap above is built: Accept/price hold follows 6a everywhere; the product
assistant (3a) prices by ZIP; the Measure screen fits without scrolling for big kitchens; the cabinets
question and plumbing disconnect options; percentage-priced protection plans; and anything else noted
as "not yet" in the LEARN docs.

## Decisions

- **Open access (2026-11-14, owner).** A signed-in person doesn't need to be invited to a store to browse and
  request a quote. Not a member of the store they're on (subdomain or store pick; else Identity:DefaultStore)?
  Identity's /me joins them as a CUSTOMER automatically: only stores with sign-up open, customer scopes only,
  a removed membership is never re-admitted, stable customer id. Identity:AutoJoin = false restores
  invitation / sign-up-link only.
- **Payments go through Stripe (2026-11-14, owner, 6d).** The old app already integrates with Stripe, so
  BuilderPro needs it too. The simulated provider (6a) stays for development and tests; Stripe Checkout +
  webhook is the real provider.
- **Sales on behalf of a customer earn commission (2026-11-13, owner, 1i).** A salesperson prepares the quote
  on the customer's behalf; acceptance and the 5 % deposit go through the customer's usual flow. The project,
  its quote and its deposit/order are credited to that salesperson, and a commission report lists each
  salesperson's accepted and paid sales (amounts from the stored quotes, never recomputed).

- **AI phone calls need their own consent (2026-11-13, owner idea, 4b).** "Phone calls" at sign-up is not
  consent to automated/AI calls. Before 4b, the sign-up and profile consent get an explicit opt-in for
  automated calls (new wording version, recorded in the consent history); without it, a person calls or
  we text instead.

- **Price hold needs a deposit (2026-11-13, owner).** A quote's price is held only once the customer
  pays an advance of **5 %** of the quote total. Accepting alone no longer holds the price: until the
  deposit is paid, the quote is re-priced like any open quote and can go outdated. Built in 6a (Accept -> pay 5 % deposit -> price held). The deposit amount comes from the pricing engine, never the browser or an agent.

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

- **Salesperson discounts (1i):** may a salesperson apply a discount (within a store limit, with a reason)?
  Default until decided: no discounts.
- **Foundry quota (deferred to production, owner 2026-11-13):** the dev gpt-4o deployment is rate-limited;
  development uses the local OpenAI-compatible LLM. Size the Foundry deployment when going to production (5a).
## Conventions

- **Angular is signal-first.** Server data comes through `httpResource` (value / isLoading /
  error / reload as signals). RxJS only where angular-auth-oidc-client requires it (its config
  loader and `logoff()`). This applies to every new page, including the agents' chat UI.

## How a deliverable is sized

- Fits in one sitting (roughly 1–2 hours of reading + running it).
- One PR, one squash commit, conventional title.
- Leaves `development` releasable: build, tests and CI all green.
