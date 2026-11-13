# LEARN 2n-f: the project workspace

**Time:** about 30 minutes · **Goal:** one place per job that a person and an agent can both work
on, where the customer alone commits.

## 1. A project is answers, not prices

`quotes.projects` (schema v4) keeps the ZIP, the product and the answers (measurements, choices) and
a stage. Every `GET /api/projects/{id}` re-prices the answers with the same guided pricing as
`/api/quotes/guided`, so prices are always today's. An **issued quote** is the snapshot.

```
discover --(product)--> configure --(Get my quote)--> quote --(Accept, customer only)--> deposit (6a)
```

## 2. The API

| Call | Who | What |
|---|---|---|
| `POST /api/projects` | customer | start at a served ZIP |
| `PUT /api/projects/{id}/answers` | customer, concierge | save product + measurements + answers; answer = the project re-priced |
| `POST /api/projects/{id}/quote` | customer, concierge | issue `Q-1001`, `Q-1002`... per store (advisory lock), valid 14 days |
| `POST /api/projects/{id}/accept` | **customer only** | hold the price; stage -> deposit; answers locked |

Accept refuses: staff (`customer_only`), an outdated quote (`quote_outdated`: answers changed since
it was issued), an expired one (`quote_expired`), and **any agent** (`agent_cannot_accept`).

## 3. Why an agent can't accept, in code

The concierge calls the platform with the customer's own token, so the token can't tell them apart.
So: (1) the concierge has no accept tool; (2) every call its tools make carries `X-BuilderPro-Agent`,
and accept refuses requests with that header. The instructions say it too, but the guarantee is the
missing tool plus the refusal.

## 4. The page

`/projects/{id}`: stage rail, the concierge column (`projectId` in its request; `save_answers` and
`issue_quote` tools; the page re-reads the project when it changed), the layout sketch (estimated),
your selection, the steps (saved as you go), the quote card with **Accept quote** / **Ask about this
quote**. Start a project creates the project when the customer picks a product. My projects and My
quotes moved to the account menu behind the avatar.
