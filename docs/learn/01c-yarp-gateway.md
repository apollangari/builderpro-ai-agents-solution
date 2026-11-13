# LEARN 1c: The YARP gateway (one front door)

**Time:** about 60 minutes · **Goal:** understand why every browser call goes through one
gateway, what it enforces, and how to see each of those checks happen.

## 1. What changed in 1c

```text
before:  Angular ──/api──▶ Me API
after:   Angular ──/api──▶ Gateway (YARP) ──▶ Me API
                            │ correlation id
                            │ rate limit (per IP, tiered)
                            │ token validated (route policy)
```

| Piece | Change |
|---|---|
| `src/Gateway/BuilderPro.Gateway` | **New.** Adapted from your `builderpro-ai-platform/platform/gateway` |
| `src/BuildingBlocks/BuilderPro.Security` | **New.** The generic OIDC setup moved here; gateway and services share it |
| AppHost | Gateway resource; Angular now references the gateway, not the API |
| `web/proxy.conf.js` | `/api` → gateway |

**What carried over from your original gateway:** auth at the edge, tiered per-IP rate limits
with 429 + Retry-After and `QueueLimit = 0`, Kestrel limits, correlation IDs, defence in depth.

**What changed:**
- **Destinations:** flat `BUILDERPRO_*_URL` env-var overrides became **Aspire service
  discovery** (`http://me-api`). The Docker env-var problem they worked around no longer exists.
- **Protecting routes:** the hand-written `IsProtected` middleware became YARP's per-route
  `AuthorizationPolicy` (`anonymous` / `default`). It's declarative and visible in config.
- **Logging and telemetry:** the Serilog + `BuilderPro.Shared` setup became ServiceDefaults'
  OpenTelemetry, which feeds the Aspire dashboard (logs, traces, metrics in one place).
- **Admin health:** `/api/admin/health` is dropped for now, since the Aspire dashboard shows
  health locally. It's an exercise below.

## 2. Concepts

### Why a gateway at all?
- **One public surface:** the browser knows one origin. Services stay private, and their
  addresses and number can change freely.
- **Cross-cutting policy in one place:** rate limits, body-size limits, auth pre-checks,
  correlation IDs.
- **Cheap rejection:** bad or anonymous traffic dies at the edge. A tested example: an
  unauthenticated `/api/me` never reaches the service.

### Defence in depth (the gateway is not the only lock)
The gateway validates the token, **and so does every service**. Zero Trust means a request is
verified wherever it lands, so a misrouted call, an internal caller or a gateway bug can't
bypass auth. That's why `BuilderPro.Security` is shared.

### Route-level authorization in YARP
In `appsettings.json` every route declares `AuthorizationPolicy`:
- `"anonymous"`: forwarded without a token (`/api/auth-config`)
- `"default"`: requires an authenticated user (`/api/me`)

Adding a route without thinking about auth is visible in review: the policy sits right there.

### Tiered rate limiting
Each **IP** gets its own counter **per tier**: `public` (300/min), `default` (100/min),
`writes` (30/min). When a budget is spent the gateway answers **429 + `Retry-After: 60`**
immediately. It never queues, because unbounded queues turn a spike into an outage.

### Correlation ID vs trace ID
- **Trace ID** (W3C `traceparent`): machine-level span linking, automatic with OpenTelemetry.
- **Correlation ID** (`X-Correlation-ID`): a human-friendly handle you can put in support
  tickets. It's kept if the caller sent a *safe* one, otherwise generated. Incoming values are
  validated (8–64 characters of `[A-Za-z0-9._-]`), because headers are untrusted input that
  end up in logs.

## 3. Code tour

| # | File | Notice |
|---|---|---|
| 1 | `src/Gateway/.../appsettings.json` | Routes, clusters, `AuthorizationPolicy` per route, `Order`, logical destination `http://me-api` |
| 2 | `.../Program.cs` | Middleware order: correlation → rate limit → authN → authZ → proxy |
| 3 | `.../CorrelationId.cs` | Generated regex; header set on request (forwarded) and response (echoed) |
| 4 | `.../RateLimiting.cs` | Partition key = tier + IP; `QueueLimit = 0` |
| 5 | `src/BuildingBlocks/BuilderPro.Security/*` | Same auth code as 1b, now shared |
| 6 | `src/AppHost/.../AppHost.cs` | `WithReference(meApi)` on the gateway; the UI references only the gateway |
| 7 | `tests/BuilderPro.Gateway.Tests/*` | A **fake backend** via `IForwarderHttpClientFactory`: test the gateway alone |

## 4. Debugging labs

### Lab A: one click, three hops in a trace (10 min)
1. Run the AppHost, sign in, and open **Who am I**.
2. Dashboard → **Traces** → the newest trace. You'll see `web` → `gateway` (`GET /api/me`) →
   `me-api` (`GET /api/me`).
3. Open the gateway span and find the `correlation.id` tag.
4. Dashboard → **Structured logs**, filter `CorrelationId = <that id>`, and see every log line
   for the request.

### Lab B: watch the gateway stop an anonymous call (5 min)
```bash
curl -i http://localhost:4200/api/me
```
You get a 401 **from the gateway**. In the trace, there's **no `me-api` span**: the request
never reached the service.

### Lab C: hit the rate limit (5 min)
1. In `src/Gateway/.../appsettings.json`, set `PublicPerMinute` to `3`, then restart the gateway
   from the dashboard.
2. Run:
   ```bash
   for i in 1 2 3 4 5; do curl -s -o /dev/null -w "%{http_code}\n" http://localhost:4200/api/auth-config; done
   ```
   You get `200 200 200 429 429`. Check the `Retry-After` header with `curl -i`.
3. Put the limit back.

### Lab D: breakpoint in the proxy pipeline (10 min)
1. Set a breakpoint in `CorrelationId.UseCorrelationId` (inside the lambda) and in
   `RateLimiting.TierFor`.
2. Run the AppHost under the debugger and click **Refresh** on **Who am I**.
3. Step through and inspect `context.Request.Headers`. Then press F5 and watch the request reach
   `MeEndpoints.GetMe` in the *other* process (Aspire attaches to both).

### Lab E: send your own correlation ID (5 min)
```bash
curl -i -H "X-Correlation-ID: support-case-1234" http://localhost:4200/api/auth-config
curl -i -H "X-Correlation-ID: bad id!" http://localhost:4200/api/auth-config
```
The first is kept; the second is replaced. Read `CorrelationIdTests` for the injection case.

### Lab F: test a gateway without its backends (5 min)
Read `GatewayFactory.cs`. YARP asks `IForwarderHttpClientFactory` for its HTTP client, and the
tests hand it a `FakeBackend` that records requests. Then run
`dotnet test tests/BuilderPro.Gateway.Tests`.

## 5. Exercises

1. **Admin health route (from the original gateway).** Add `/api/admin/health` that probes each
   cluster's `/health` with a 3 s timeout, in parallel, staff role only. Test it with the fake
   backend.
2. **Writes tier.** Add a `POST /api/me/preferences` endpoint to Me API and a gateway route.
   Prove with a test that it's limited at `WritesPerMinute`.
3. **Per-user limits.** Partition authenticated traffic by `sub` instead of IP. When is that
   better, and when is it worse (think of shared office IPs vs one abusive account)?
4. **Request-size limit.** Lower `MaxRequestBytes` to 10, then POST a bigger body and observe
   **413**.

## 6. Review questions

1. *If the gateway validates tokens, why does Me API validate them too?*
   > Zero Trust / defence in depth. Services can be reached by other paths (internal calls,
   > misconfiguration, a future gateway bug). Each service must protect itself.
2. *Why `QueueLimit = 0`?*
   > Queuing under overload increases latency for everyone and memory on the gateway. Fast 429s
   > tell clients to back off and keep the system healthy.
3. *Why validate an incoming correlation ID instead of trusting it?*
   > It's attacker-controlled input written to logs and forwarded to other systems: log
   > injection, header injection, oversized values.
4. *What does service discovery buy you in the gateway config?*
   > Destinations are logical names. Ports and hosts change per environment without config edits
   > or code changes, which removes the class of bugs your original gateway had to work around.
5. *Where would you put response caching for public catalog reads later?*
   > Gateway (YARP output caching) for cross-user public data; never for per-user responses.

## 7. Further reading

- YARP docs (routes, clusters, authorization, transforms): https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/yarp-overview
- ASP.NET Core rate limiting middleware: https://learn.microsoft.com/aspnet/core/performance/rate-limit
- Aspire service discovery: https://aspire.dev
- W3C Trace Context: https://www.w3.org/TR/trace-context/
