# LEARN 6a: the deposit holds the price

**Owner decision:** a quote's price is held only once the customer pays an advance of **5 %**.

```
quote --(Accept, customer)--> deposit due --(pay 5 %, customer)--> measure visit (price held, answers locked)
```

| Step | Rule |
|---|---|
| Accept | Records the customer's yes. **Doesn't hold the price**: until the deposit is paid the quote is re-priced on every read and can go outdated or expire. Changing answers sends the project back to the quote step. |
| Start deposit `POST /api/projects/{id}/deposit` | Customer only, never an agent (`agent_cannot_pay`) or staff. Quote must be accepted, current and not expired. Amount = `DepositPolicy` (5 % of the stored total, rounded to cents; `Quotes:DepositPercent:{tenant}` can change it). One pending deposit per quote. |
| Pay | Provider `simulated` (dev/tests): our test-mode page `/pay/{id}` and `POST /api/deposits/{id}/pay`. A real provider (Stripe Checkout) confirms by webhook instead; the simulated endpoint is off then. |
| Paid | The quote is **held** (`held: true`, always current), the project moves to `measure`, answers and re-quotes are refused (`project_locked`). |

Tables: `quotes.deposits` (schema v5), at most one paid deposit per quote (unique index).

6d replaced pending/paid with the payment state machine (created, processing, succeeded, failed, unknown) and added Stripe: see [06d](06d-stripe-payments.md).
