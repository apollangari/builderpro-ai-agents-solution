# LEARN 1h: packages per store

**Time:** about 15 minutes · **Goal:** a store's package decides which experience its customers get,
and the server enforces it.

## 1. The features

| Feature | What the customer gets |
|---|---|
| `guided` | The step-by-step flow: ZIP, type, product, measure, options, protection, quote. Always on. |
| `help-chat` | A chat that **only answers** questions about products and steps (and filters the catalog view). No tools that change anything. |
| `concierge` | The AI concierge that fills in the project, prices drafts and issues the numbered quote (never accepts or pays). |
| `phone-scheduling` | AI phone calls to book the measure visit (4b, not built yet). |

Configured per store in Identity (`Identity:Stores[].Features`); a store without a list gets
`guided + help-chat + concierge`. Dev seed: **Acme** has all three, **Globex** has guided + help chat.

## 2. How it travels

`GET /api/identity/me` returns the caller's store **and its features**; the shared tenant lookup
(`TenantLookup.HasFeature`) carries them to every service, cached with the rest (2f). The store's
public info (`/api/identity/stores/{id}`) includes them too, for subdomains.

## 3. Where it's enforced

| Request | concierge | help-chat only | neither |
|---|---|---|---|
| Concierge draft (`/assistant` page) | yes | 403 `feature_not_included` | 403 |
| Chat on a project | full tools (save answers, issue quote) | answers only (find products, read steps) | 403 |
| Chat beside the catalog | yes | yes | 403 |
| Product Q&A (`/api/assistant/chat`) | yes | yes | 403 |

The model is never called for a refused request. The web hides what isn't included (no concierge button on
Home, a "Help" chat instead of "Concierge", no chat column at all), but the server is what counts.
