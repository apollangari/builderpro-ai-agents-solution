# LEARN 1a: Walking skeleton (Aspire + Angular → API)

**Time:** about 45–60 minutes · **Goal:** understand how the pieces start, find each other, and
show up in one dashboard. Every later increment builds on exactly this.

## 1. Concepts

### Walking skeleton
The thinnest slice that runs **end to end through the real architecture**: UI → API, started
together, observable, tested and in CI. It does almost nothing on purpose. Later increments add
muscle to bones that already work.

### Aspire AppHost = your system, described in code
`src/AppHost/BuilderPro.AppHost/AppHost.cs` lists every resource (services, databases, the
Angular app) and how they relate. `dotnet run` on it:
- starts each resource (projects, `npm start`, containers),
- assigns ports and **injects each resource's address** into the others (service discovery),
- runs the **dashboard**: resources, console logs, structured logs, traces and metrics.

> The AppHost is for **local development** and describing the deployment. It is not a production
> runtime.

### ServiceDefaults = one line of cross-cutting behaviour
`builder.AddServiceDefaults()` gives every service the same OpenTelemetry, health endpoints
(`/health` ready, `/alive` live), service discovery and resilient `HttpClient`s.

### Service discovery
The Angular dev server doesn't know the API's port. Aspire passes it as the environment variable
`services__me-api__http__0`, and `web/proxy.conf.js` reads that. .NET services do the same through
`http://me-api` URLs. **No hard-coded ports anywhere.**

### Central Package Management
`Directory.Packages.props` holds every NuGet version once. Projects say *what* they use; that file
says *which version*. Upgrades are one-line, reviewable changes.

### Modern Angular (22)
Standalone components (no NgModules), `inject()`, **signals** for state, `computed()` for
derived state, `@switch`/`@if` control flow, and **zoneless** change detection (no `zone.js`).
Signals tell Angular exactly what changed.

### TimeProvider
.NET's built-in clock abstraction. Production uses `TimeProvider.System`; tests inject a
`FakeTimeProvider`, so time-based code is deterministic.

## 2. Code tour (read in this order)

| # | File | Notice |
|---|---|---|
| 1 | `Directory.Build.props` | Rules for every project: `TreatWarningsAsErrors`, nullable, analyzers |
| 2 | `Directory.Packages.props` | All versions; the Aspire "same family" rule |
| 3 | `src/AppHost/BuilderPro.AppHost/AppHost.cs` | The system in about 15 lines: `AddProject`, `AddJavaScriptApp`, `WithReference`, `WaitFor` |
| 4 | `src/ServiceDefaults/.../Extensions.cs` | What *every* service gets; why health probes are filtered out of traces |
| 5 | `src/Services/Me/BuilderPro.Me.Api/Program.cs` | A whole service in about 10 lines |
| 6 | `.../MeEndpoints.cs` | Minimal API; `ClaimsPrincipal` and `TimeProvider` injected as parameters |
| 7 | `web/proxy.conf.js` | How Angular finds the API |
| 8 | `web/src/app/features/whoami/whoami.page.ts` | Signals + a discriminated-union state (`loading`/`loaded`/`error`) |
| 9 | `tests/.../MeEndpointsTests.cs`, `whoami.page.spec.ts` | `WebApplicationFactory`, `FakeTimeProvider`, `HttpTestingController` |
| 10 | `.github/workflows/ci.yml` | Two jobs; failing builds post their errors on the PR |

## 3. Debugging labs

### Lab A: break inside the API from the UI (10 min)
1. Start the AppHost **under the debugger** (VS: F5 on `BuilderPro.AppHost`; VS Code: **AppHost (Aspire)**).
   Aspire attaches the debugger to `me-api` automatically.
2. Set a breakpoint in `MeEndpoints.GetMe`.
3. Open the `web` URL and click **Refresh**. Execution stops in C#.
4. Inspect `user.Identity.IsAuthenticated` (false, since there's no login yet) and `clock`.

### Lab B: follow one click as a trace (10 min)
1. In the dashboard, open **Traces**.
2. Click **Refresh** in the UI, then find the newest `GET /api/me` trace.
3. Open it and note the span's duration, the `http.route` attribute and the `me-api` resource.
   From 1c onwards the same trace grows: web → gateway → identity → API.

### Lab C: watch Aspire's service discovery (5 min)
1. Dashboard → **Resources** → `web` → **Environment variables**.
2. Find `services__me-api__http__0`. That's the address `proxy.conf.js` uses.
3. Stop `me-api` from the dashboard (⏹), click **Refresh** in the UI, and watch the error state
   appear. Start it again, and the UI recovers.

### Lab D: debug an Angular test (5 min)
1. In `whoami.page.spec.ts`, add `debugger;` after `fixture.detectChanges();`.
2. Run `npm run test:watch` in `web/`, then open the Vitest UI or attach the VS Code JavaScript debugger.
3. Inspect `fixture.componentInstance` and watch the `state()` signal move from `loading` to `loaded`.

### Lab E: make CI tell you what broke (5 min)
On a feature branch, add an unused variable in `MeEndpoints.cs` and open a PR. The build fails
(warnings are errors), and a **comment with the error lines** appears on the PR.

## 4. Exercises

1. **Add `/api/me/version`** returning the assembly's informational version. Test first.
   *Hint:* `typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()`.
2. **Show the version in the UI** under the card. Use a second signal, and handle its error separately.
3. **A readiness check:** add a fake health check without the `live` tag that is unhealthy for
   the first 10 seconds after start (use `TimeProvider`). Watch `/health` vs `/alive`, and the
   dashboard's health column.
4. **Add a second API** to the AppHost (copy `Me.Api` → `Echo.Api`) and call it *from* `me-api`
   with `HttpClient` + `http://echo-api`. Then find the two-hop trace.

## 5. Review questions

1. *Why does the UI call `/api/me` (relative) instead of `http://localhost:5080/api/me`?*
   > The UI shouldn't know topology. In dev a proxy decides, and from 1c the gateway owns `/api`.
   > Hard-coded hosts break across environments and leak internal addresses.
2. *What's the difference between `/alive` and `/health`?*
   > Liveness = restart me if this fails (process-level only). Readiness = don't send me traffic
   > yet (includes dependencies). Mixing them causes restart storms during a database outage.
3. *Why `TimeProvider` instead of `DateTime.UtcNow`?*
   > Testability and determinism. Expiry rules (quotes, confirmation tokens) must be testable to
   > the second without `Thread.Sleep`.
4. *What does the AppHost not do in production?*
   > It doesn't run there. It describes the app; deployment tooling turns that into Azure
   > resources (or you use Terraform, as we will).
5. *Why put package versions in one file?*
   > Consistency (no two services on different OpenTelemetry versions), reviewable upgrades, and
   > the ability to enforce rules like "all Aspire packages on one version".

## 6. Further reading

- Aspire overview and what's new in 13.x: https://aspire.dev
- JavaScript apps in Aspire (`AddJavaScriptApp`): https://devblogs.microsoft.com/aspire/aspire-for-javascript-developers
- Angular signals: https://angular.dev/guide/signals
- Central Package Management: https://learn.microsoft.com/nuget/consume-packages/central-package-management
- `TimeProvider`: https://learn.microsoft.com/dotnet/standard/datetime/timeprovider-overview
