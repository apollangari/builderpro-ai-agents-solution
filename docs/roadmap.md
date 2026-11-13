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

## Next

| # | Deliverable | Done when |
|---|---|---|
| 0a | This roadmap; README stack version | Merged |
| 0b | Resolve the `ASPIRE010` build warning | Build has 0 warnings |
| 2a | **Catalog service skeleton**: own DB (`catalogdb`), tenant-scoped `GET /api/catalog/products` through the gateway, seeded in Development | Endpoint returns only the caller's tenant's products; tests cover cross-tenant isolation |
| 2b | **Catalog in Angular**: product list page behind login | Alice sees her retailer's products |
| 2c | **Pricing as pure domain code**: `Quote` from product lines + quantities, money as decimal with currency, rounding rules in one place | Unit tests for totals, rounding and invalid input; no HTTP yet |
| 2d | **Quote API**: `POST /api/quotes` (validate, price, persist) | Integration tests; same request → same price |
| 3a | **First agent, read-only**: .NET Agent Framework agent that answers product questions using catalog tools only | Agent never states a price it didn't get from a tool |
| 3b | **Agent drafts a quote**: agent proposes lines, the Quote API prices them, a human approves | The agent cannot approve its own quote |
| 3c | Expose catalog/quote tools over **MCP** | An MCP client can list and call the tools |
| 4a | Python **LangGraph** agent talking to the .NET agent over **A2A** | One cross-agent conversation, traced end to end in the dashboard |
| 5a | Azure: Terraform for one environment, deploy `development` | `dev` environment reachable, Entra login |

## How a deliverable is sized

- Fits in one sitting (roughly 1–2 hours of reading + running it).
- One PR, one squash commit, conventional title.
- Leaves `development` releasable: build, tests and CI all green.
