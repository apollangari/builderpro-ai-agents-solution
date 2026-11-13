# LEARN 3a: a read-only product assistant

**Time:** about 40 minutes · **Goal:** put an LLM agent in front of the platform without letting it
invent prices, place orders or see another store's data.

## 1. The shape

```
Angular /assistant ──POST /api/assistant/chat──▶ Gateway ──▶ Assistant service
                                                              │  ChatClientAgent (Microsoft Agent Framework)
                                                              │    instructions + 4 tools
                                                              ├──▶ Catalog  (list_categories, search_products)
                                                              ├──▶ Quotes   (estimate_roof, price_products)
                                                              └──▶ the model (Foundry gpt-4o, or a local LLM)
```

- The server keeps **no chat state**: the browser sends the whole conversation (max 20 turns,
  2000 chars each) and the last turn must be the user's.
- The tools call Catalog and Quotes **as the caller**: the Authorization and `X-BuilderPro-Tenant`
  headers are forwarded, so the agent can only see what the signed-in user could see.
- Before the model is called, the tenant is resolved like in every other service. A non-member gets
  403 and the model is never called (no tokens spent).

## 2. The price rule, enforced twice

1. **Instructions:** "NEVER calculate money; use price_products." Quantities come from
   `estimate_roof`; totals come from the Quote API's `QuotePricer`, the same code path as a real quote.
2. **PriceGuard (code, not a prompt):** every number in a tool result whose property name looks like
   money (`price`, `total`, `tax`, `subtotal`, `amount`) is recorded in a `PriceLedger`. Every money
   amount in the model's reply (`$1,234.56`, `USD 12.5`, `12.50 dollars`) must be in the ledger, or the
   whole reply is replaced with a safe message and `guarded: true`. The UI drops guarded replies from
   the history it sends back, so a bad answer can't steer the next turn.

The prompt makes good answers likely; the guard makes a bad price impossible to deliver.

## 3. Choosing the model (AppHost user secrets only)

| Provider | Parameters | Notes |
|---|---|---|
| Microsoft Foundry (default) | `foundry-endpoint`, `foundry-deployment`, `foundry-api-key` | Uses the OpenAI v1 API at `https://<host>/openai/v1/`; a project URL works too (only the host is used) |
| Any OpenAI-compatible server | `assistant-provider=local`, `local-llm-endpoint`, `local-llm-model`, optional `local-llm-api-key` | Ollama, LM Studio, vLLM, FreeLLMAPI. Pick a model that supports **tool calling** |

```bash
cd src/AppHost/BuilderPro.AppHost
dotnet user-secrets set "Parameters:assistant-provider" "local"
dotnet user-secrets set "Parameters:local-llm-endpoint" "http://localhost:3001/v1"
dotnet user-secrets set "Parameters:local-llm-model" "<model name>"
dotnet user-secrets set "Parameters:local-llm-api-key" "<key>"
```

Remove `assistant-provider` (`dotnet user-secrets remove "Parameters:assistant-provider"`) to go back
to Foundry. With nothing configured the endpoint answers 503 `assistant_unavailable`, never a crash.

A local gateway that forwards to free third-party tiers sends prompts to those providers: use it
for development only, never with real customer data.

## 4. When the model fails

| Model answer | Assistant answers | UI says |
|---|---|---|
| HTTP 429 (quota used up) | 503 `assistant_busy` | "The AI model is busy right now…" |
| Any other model error | 502 `assistant_model_error` | "The AI model couldn't answer…" |

The SDK retries once, then gives up (the default backs off for about 90 s, which looks like a hang).
A tool round trip is **two** model calls, so a deployment with a very low tokens-per-minute limit
answers the first and rate-limits the second: raise the limit on the Foundry deployment.

## 5. Seeing it work

Every model call and tool call is a span in the Aspire dashboard (`UseOpenTelemetry`, source
`BuilderPro.Assistant`): open the trace of `POST /api/assistant/chat` to see the calls to Catalog and
Quotes between the model calls.

## 6. Try it

- Ask "How much is 30 sq ft of the Calacatta quartz?" and compare the total with `/quote`.
- In a test, make `ScriptedModel` answer with an amount no tool returned and watch the guard replace it.
