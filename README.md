# BuilderPro AI Agents Solution

An **agent-first** home-improvement platform, built as a **learning kit for senior developers**:
small, working increments, each with its own `LEARN` guide and debugging labs.

**Stack:** .NET 10 microservices · Aspire 13.6 · Angular 22 · Keycloak (dev) / Entra (prod) ·
YARP · Python LangGraph 1.2.21 + Microsoft Agent Framework (.NET) · MCP · A2A · Microsoft Foundry ·
Terraform.

## The one rule

> **Agents decide; the domain enforces.** An LLM may interpret, choose and phrase. It never
> computes a price, invents a date, or approves its own side effect.

## Run it

Prerequisites: [docs/setup.md](docs/setup.md) (.NET 10 SDK, Node 22, Docker Desktop).

```bash
cd web && npm install && cd ..
dotnet run --project src/AppHost/BuilderPro.AppHost
```

Open the **Aspire dashboard** URL printed in the terminal, then http://localhost:4200.
Sign in as `alice` / `Passw0rd!`. Five more scenario users (two tenants, no tenant, admin) are in [infra/keycloak](infra/keycloak/README.md).

## Repository map

```text
BuilderPro.slnx                 one solution, all .NET projects
Directory.Build.props           compiler rules for every project (warnings = errors)
Directory.Packages.props        every NuGet version, in one place
src/AppHost/                    Aspire: starts the whole system locally
src/ServiceDefaults/            telemetry, health, discovery, resilience: one line per service
src/BuildingBlocks/             shared building blocks (Security: generic OIDC/JWT)
src/Gateway/                    YARP gateway: the only door for /api/*
src/Services/Me/                tiny API: /api/auth-config (public), /api/me (needs a token)
src/Services/Identity/          tenant memberships (own Postgres database: identitydb)
infra/keycloak/                 local identity provider realm (dev only)
tests/                          xUnit tests per service
web/                            Angular 22 app (standalone, signals, zoneless)
docs/                           setup, branching, learning guides, ADRs
```

## Increments

| # | Increment | Status | Learn |
|---|---|---|---|
| 1a | Walking skeleton: Aspire + Angular → API | ✅ | [docs/learn/01a-walking-skeleton.md](docs/learn/01a-walking-skeleton.md) |
| 1b | Keycloak + Angular login + API validates the token | ✅ | [docs/learn/01b-keycloak-login.md](docs/learn/01b-keycloak-login.md) |
| 1c | YARP gateway in front | ✅ | [docs/learn/01c-yarp-gateway.md](docs/learn/01c-yarp-gateway.md) |
| 1d | Identity service + claims normalisation + memberships | ✅ | [docs/learn/01d-identity-memberships.md](docs/learn/01d-identity-memberships.md) |

## How we work

Feature branches → `development` → `staging` → `main`, with hotfixes on their own path.
See [docs/branching.md](docs/branching.md).

What comes next: [docs/roadmap.md](docs/roadmap.md).
