# LEARN 3b: the concierge

**Time:** about 30 minutes · **Goal:** an agent that does real work for the customer (building a
quote) while it is technically unable to commit to anything on their behalf.

## 1. The shape

```
/assistant (chat | draft quote)
   │  POST /api/assistant/concierge { messages, accountZip, draft }
   ▼
Assistant service ── ChatClientAgent "builderpro-concierge"
   ├─ check_zip ............ GET  /api/catalog/service-area?zip=
   ├─ list_project_types ... GET  /api/catalog/products?zip=  (+ categories)
   ├─ find_products ........ GET  /api/catalog/products?zip=&category=
   ├─ get_project_steps .... GET  /api/catalog/products/{sku}/options?zip=
   └─ price_draft .......... POST /api/quotes/guided/preview      (never /api/quotes/guided)
   ▼
{ reply, toolsUsed, draft, quote }  -> the page shows the draft; the CUSTOMER presses Save quote
```

## 2. Three ways it is kept honest

| Risk | Defence |
|---|---|
| The model invents or rounds a price | Price guard (3a): every amount in the reply must be one a tool returned this turn |
| The model miscalculates sq ft from "10 ft 6 in" | `price_draft` takes feet + inches; **code** converts to inches; the quote API measures |
| The model "accepts" or saves for the customer | It has no tool that saves. Saving is `POST /api/quotes/guided`, called by the page's button |

The instructions say the same things, but the guarantees come from the code, not the prompt.

## 3. No chat state on the server

The page sends the conversation (last 20 turns), the ZIP from the account (`postal_code` claim) and
the current draft (the last guided request the concierge priced). The draft goes to the model as
context, so "make it bullnose instead" works without a database. The draft holds answers only;
prices are recomputed by the pricing engine every time.

## 4. Try it

With the local LLM (`auto:freellm`) or Foundry: sign in as alice, open **Concierge**, and say
"I want new quartz countertops". Answer the ZIP question, give "10 ft 6 in by 25 in", pick an edge,
a sink and a plan; watch the draft fill in on the right; press **Save quote**.

## 5. Next

- 2n-f: the draft becomes a server-side project, so the chat and the step-by-step pages share it.
- 3d: estimates from photos feed `price_draft` with "estimated" measurements.
- 3e: a person can take over the conversation.
