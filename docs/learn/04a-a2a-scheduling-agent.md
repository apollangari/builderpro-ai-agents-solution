# LEARN 4a: a Python LangGraph agent over A2A

The concierge (.NET, Microsoft Agent Framework) asks a second agent (Python, LangGraph) for measure-visit times. The two talk over **A2A 1.0**, and auth is **on-behalf-of** at every hop.

```
customer --(token aud: builderpro-assistant)--> Assistant (.NET)
   Assistant --token exchange (aud builderpro-scheduling)--> Keycloak
   Assistant --A2A SendMessage + that token--> scheduling agent (Python)
      agent: validates the token (issuer, audience, signature, expiry)
      agent --token exchange (aud builderpro-api)--> Keycloak
      agent --GET project / visit slots / profile--> gateway --> Quotes / Identity
   <-- task artifact: {outcome, proposals[≤3], message}
```

## The scheduling agent (`agents/scheduling`)

| Part | What |
|---|---|
| `app/graph.py` | LangGraph `load_context -> choose_slots -> respond` |
| `app/agent.py` | A2A executor and agent card (skill `propose_visit_times`, input `{"projectId": …}`) |
| `app/main.py` | Starlette app: `/.well-known/agent-card.json` (public), `/a2a` (JSON-RPC, bearer required), `/health`, OpenTelemetry over OTLP |
| `app/auth.py` | Bearer validation against Keycloak's JWKS, and the RFC 8693 exchange |
| `app/platform.py` | Gateway calls with the exchanged token and `X-BuilderPro-Agent: scheduling` |

### The graph

- **`load_context`:** reads the project.
  - Not at the measure step → `not_ready`.
  - A visit is already booked → `booked`.
  - Otherwise it reads the open visit times and the customer's contact hours from their profile.
- **`choose_slots`:** keeps open times inside the contact hours, in store time:

  | Contact hours | Window |
  |---|---|
  | business | 8–17 Mon–Fri |
  | evenings | 17–21 |
  | weekends | Sat–Sun |
  | anytime | any |

  It proposes up to 3, each on a different day; if none fit, the outcome is `no_fit`.
- **`respond`:** writes plain text. **It never books**: the customer confirms a time on the project page.
- **No model:** dates and times come from the platform, so there's nothing for a model to invent.

### Security practices

- Only tokens issued **for the agent** are accepted. A customer's own token gets 401.
- Every outbound call uses an exchanged token, never the token the agent received.
- Settings are checked at start-up: HTTPS required except for a local identity provider in development, and the secret must be set.
- The exchange answer is validated. Errors are sanitised, and the A2A task fails with a safe message.

## .NET side

- **Concierge tool:** `propose_visit_times`, in project mode only, and only when `Assistant:SchedulingAgent:Enabled` is set.
- **Client:** `A2ASchedulingAgent` sends an A2A 1.0 `SendMessage` (header `A2A-Version: 1.0`, a `data` part) and reads the task's data artifact.
  - Failures come back to the model as data it can explain: `token_exchange_failed`, `scheduling_agent_unavailable`, the agent's own failure message.
- **Exchange:** `ITokenExchange.ForAudienceAsync(token, audience)` gets a token for any downstream audience. Under `entra-obo` it uses `api://{audience}/.default`.

## Wiring

| Where | What |
|---|---|
| Aspire | `AddUvicornApp("scheduling-agent", "agents/scheduling", "app.asgi:app").WithPip()`, with `AUTH_AUTHORITY`, `SCHEDULING_CLIENT_SECRET`, `PLATFORM_BASE_URL` = the gateway |
| Assistant | Gets the agent's address and its tool |
| Telemetry | Both sides export to the dashboard and trace context flows over HTTP, so a concierge turn is one trace across .NET and Python |
| Keycloak | `builderpro-scheduling`: confidential, token exchange on, adds the `builderpro-api` audience. `builderpro-assistant` may also request `builderpro-scheduling`. |
| CI | A `Python agents test` job runs the agent's pytest suite |

## Live-tested

Run against the running app:
- Alice's token was exchanged by the Assistant's client for an agent token (`aud builderpro-scheduling`).
- The A2A `SendMessage` completed. The agent exchanged again and read her measure-stage project through the gateway, then answered that the visit was already booked.
- Her own token sent to the agent got 401.

## Not yet

- Streaming (`SendStreamingMessage`).
- The agent calling the .NET agent back.
- A persistent task store; it's in memory.
