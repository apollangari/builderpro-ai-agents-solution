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

## Next

| # | Deliverable | Done when |
|---|---|---|
| 2f | **Redis for hot reads**: cache the tenant lookup (invalidated on revoke) and each tenant's product list (invalidated on change) | Identity/Catalog calls drop on repeat requests; revoke still takes effect immediately |
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
