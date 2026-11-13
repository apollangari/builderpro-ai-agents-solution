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
| 2m | **Quote builder UI**: "Add to quote" from picker/compare, a draft list with quantities, the roof calculator filling bundles/rolls/labour from the estimator, submit -> `POST /api/quotes`, and a "My quotes" page | Component tests; a live roof quote end to end |
| 3a | **First agent, read-only**: .NET Agent Framework agent that answers product questions using catalog tools only | Agent never states a price it didn't get from a tool |
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

## Open questions

- **3a LLM provider (blocks the agents):** Microsoft Foundry / Azure OpenAI (matches the .NET Agent Framework + Azure plan) or Anthropic Claude, which model, and where the key lives (user secrets locally, Key Vault in Azure). Needs the owner's account and budget.

## Conventions

- **Angular is signal-first.** Server data comes through `httpResource` (value / isLoading /
  error / reload as signals). RxJS only where angular-auth-oidc-client requires it (its config
  loader and `logoff()`). This applies to every new page, including the agents' chat UI.

## How a deliverable is sized

- Fits in one sitting (roughly 1–2 hours of reading + running it).
- One PR, one squash commit, conventional title.
- Leaves `development` releasable: build, tests and CI all green.
