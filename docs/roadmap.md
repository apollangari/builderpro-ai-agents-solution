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

## Next

| # | Deliverable | Done when |
|---|---|---|
| 2h | **Product categories + attributes** (Catalog): fixed category list (countertops, roofing, sheds, flooring, decking, cabinets, paint, windows-doors, fencing, labour), each with attribute definitions (countertop: material, colour, finish, thickness; shingles: style, colour, wind rating, bundles per square; shed: size, material, roof style). Schema v2: `category`, `attributes jsonb`, `image_url`. `GET /api/catalog/categories`, `GET /api/catalog/products?category=`. Seed several products per category for t-acme and t-globex | Tests: filter, unknown category, tenant isolation still holds |
| 2i | **Sample images**: generated SVGs (stone textures, shingle swatches, shed drawings) in `web/public/products/`, referenced by `imageUrl`. No copied web photos (licensing) | Every seeded product has an image |
| 2j | **Product picker UI**: category tabs, attribute filters, image cards; signal-first, Navy & Sand tokens | Component tests; 44 px targets |
| 2k | **Compare**: pick 2-4 products of one category, side-by-side attributes, price per unit and a rough total for a typical size (computed by domain code, not the UI) | Tests |
| 2l | **Visualizer for EVERY product type**: with no upload, show a **default scene per category** with the selected product applied (kitchen for countertops/cabinets, house roof for shingles, backyard for sheds/decking/fencing, room floor for flooring, wall for paint, house front for windows-doors). Scenes are generated SVG with named surfaces the product texture/colour fills. With an upload: customer drags 4 corners onto the surface and the texture is drawn with perspective (canvas homography); photo stays in the browser. AI auto-detection later | Every category has a default scene; tests for the homography maths and scene/category mapping; manual check |
| 2g | **Roof estimator** (domain code): roof area + pitch + waste -> squares, bundles, rolls | Unit tests; quantities never come from an LLM |
| 3a | **First agent, read-only**: .NET Agent Framework agent that answers product questions using catalog tools only | Agent never states a price it didn't get from a tool |
| 3b | **Agent drafts a quote**: agent proposes lines, the Quote API prices them, a human approves | The agent cannot approve its own quote |
| 3c | Expose catalog/quote tools over **MCP** | An MCP client can list and call the tools |
| 4a | Python **LangGraph** agent talking to the .NET agent over **A2A** | One cross-agent conversation, traced end to end in the dashboard |
| 5a | Azure: Terraform for one environment, deploy `development` | `dev` environment reachable, Entra login |

## Conventions

- **Angular is signal-first.** Server data comes through `httpResource` (value / isLoading /
  error / reload as signals). RxJS only where angular-auth-oidc-client requires it (its config
  loader and `logoff()`). This applies to every new page, including the agents' chat UI.

## How a deliverable is sized

- Fits in one sitting (roughly 1–2 hours of reading + running it).
- One PR, one squash commit, conventional title.
- Leaves `development` releasable: build, tests and CI all green.
