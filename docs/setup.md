# Setup

## 1. Install (once)

| Tool | Version | Check |
|---|---|---|
| .NET SDK | 10.0 (any 10.0.1xx or newer feature band) | `dotnet --version` |
| Node.js | 22 LTS | `node --version` |
| Docker Desktop | latest, **running** (Keycloak runs as a container from 1b) | `docker info` |
| Git | any | `git --version` |
| IDE | Visual Studio 2026, **or** VS Code + C# Dev Kit + the Aspire extension, **or** Rider | |

Aspire needs **no workload install** since version 9. The AppHost project pulls the
`Aspire.AppHost.Sdk` from NuGet automatically.

Optional, but nice: the Aspire CLI (`aspire run`). Install it from https://aspire.dev.

## 2. Get the code

```bash
git clone https://github.com/apollangari/builderpro-ai-agents-solution.git
cd builderpro-ai-agents-solution
git switch development
```

## 3. First run

```bash
cd web && npm install && cd ..        # once, and after package.json changes
dotnet run --project src/AppHost/BuilderPro.AppHost
```

1. The terminal prints `Login to the dashboard at http://localhost:15080/login?t=...`. Open it.
2. On the **Resources** page, wait until `postgres`, `keycloak`, `identity`, `me-api`, `gateway` and `web` are **Running / Healthy**.
   The first start downloads the Keycloak image (about a minute).
3. Open http://localhost:4200, click **Sign in**, and use `alice` / `Passw0rd!`. Then open **Who am I**.

**Visual Studio:** set `BuilderPro.AppHost` as the startup project, then press F5.
**VS Code:** Run and Debug → **AppHost (Aspire)**.

## 4. Run the tests

```bash
dotnet test BuilderPro.slnx
cd web && npm test
```

## 5. Troubleshooting

| Symptom | Fix |
|---|---|
| `A compatible .NET SDK was not found` | Install the .NET 10 SDK. `global.json` accepts any 10.0 feature band. |
| `web` stays in *Starting* | Run `npm install` inside `web/` first; check the `web` logs in the dashboard. |
| The card says "Could not reach the API" | Is `me-api` green in the dashboard? Check its **Console logs**. |
| Port already in use | Another AppHost is still running. Stop it, or change the ports in `launchSettings.json`. |
| HTTPS certificate warnings | `dotnet dev-certs https --trust` |
| `identity` fails at startup | Is `postgres` healthy? Check the `identity` console logs. A missing connection string means it wasn't started by the AppHost. |
| Who am I: platform view says 403 `no_membership` for alice | The dev seed runs only in Development, and keys use the issuer URL. If you changed Keycloak's port or realm name, the keys won't match. |
| `keycloak` unhealthy / restarting | Docker running? Port 8080 free? Read its **Console logs** in the dashboard; a realm-import error names the bad field. |
| Login page says *Invalid parameter: redirect_uri* | The app must be on **http://localhost:4200** exactly (not 127.0.0.1, not another port). |
| **Who am I** shows 429 | You hit the gateway rate limit (see LEARN 1c, Lab C). Wait a minute or raise the limit in the gateway's `appsettings.json`. |
| **Who am I** shows 401 | See LEARN 1b, Lab C: usually the audience mapper, or the API's `Auth__Authority` differs from the token's `iss`. |
| API won't start: `Auth:Authority is not configured` | Run through the AppHost, or set it in `appsettings.Development.json`. |
