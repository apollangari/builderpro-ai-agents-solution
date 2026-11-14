# LEARN 6d: Stripe payments and the payment state machine

**Owner:** the old app integrates with Stripe, so BuilderPro does too. Every payment also follows an explicit **state machine**.

## The state machine

```
created ──> processing ──> succeeded   (confirmed by the provider)
   │             ├──────> failed       (confirmed: declined, refused, checkout expired)
   └─> failed    └──────> unknown      (timeout: we don't know what the provider did)
                              │
                     reconciliation (status lookup / webhook)
                              ├──> succeeded
                              ├──> failed
                              └──> processing  (the checkout exists and is still open)
```

- **Rules** live in `PaymentStates` (Quotes.Domain). Succeeded and failed are final.
  - A new try after a failure is a **new deposit**, so each attempt keeps its own history.
- **Moves** happen in one place, `PaymentFlow`, called by the endpoints, the webhook and the reconciler.
  - A move is a conditional update (`WHERE status = @from`), so two webhooks can't both win.
- **History:** every move is written to `quotes.payment_events` with its source and detail. The sources are `checkout`, `webhook`, `lookup` and `simulated`. Read it with `GET /api/deposits/{id}/events`.
- **Who can mark a payment succeeded:** only the provider. That means Stripe's signed webhook, our own status lookup, or (in development) our test page. **The browser never can.**
- **Amount check:** succeeded requires the provider's amount and currency to equal the deposit's. A mismatch is logged for a person and **never** marked paid.

## Providers (`Payments:Provider`)

| | simulated (default: dev and tests) | stripe |
|---|---|---|
| Checkout | our page `/pay/{id}`: **Pay**, or **Decline** to test a failure | Stripe Checkout session (`/v1/checkout/sessions`), amount in cents, deposit and tenant in metadata |
| Confirmed by | the test page (customer only) | **signed webhook** `POST /api/payments/stripe/webhook`, or a status lookup |
| Retry safety | n/a | `Idempotency-Key` per attempt: a retry after a timeout gets the **same** session, never a second charge |

## Stripe webhook

- **Signature:** the `Stripe-Signature` header (`t=…,v1=…`) is an HMAC-SHA256 of `t.body` with the endpoint secret. We compare in constant time.
  - Anything older than 5 minutes is refused, so replayed requests don't work.
  - Several `v1` values are accepted, so the secret can be rolled.
- **Events handled:**
  - `checkout.session.completed` or `async_payment_succeeded`, when paid → **succeeded**.
  - `async_payment_failed` or `checkout.session.expired` → **failed**.
  - Anything else is acknowledged and ignored.
- **Gateway:** this is the one anonymous route on the gateway. The signature is the authentication.

## Reconciliation

- **Background:** `PaymentReconciler` runs every `Payments:ReconcileSeconds` (30). It looks at payments that are `unknown`, or `processing` after their checkout expired (a missed webhook).
  - If no session was recorded, it retries the checkout with the **same** idempotency key and request, which finds the session if Stripe made one.
  - Otherwise it asks Stripe `GET /v1/checkout/sessions/{id}`.
- **On return:** coming back from Stripe (`/pay/{id}?return=stripe`), the page calls `GET /api/deposits/{id}?refresh=true` every 2 s. The server asks Stripe when the webhook hasn't arrived yet.
- **Starting the deposit again:** if a payment is still waiting for an answer, it's reconciled first. A second checkout never opens while one may still be paid.

## Setting up Stripe (test mode)

1. In the Stripe dashboard (test mode), copy the secret key `sk_test_…`.
2. Install the Stripe CLI, then run:

   ```bash
   stripe listen --forward-to http://localhost:<gateway port>/api/payments/stripe/webhook
   ```

   It prints the webhook secret `whsec_…`.
3. Save both as AppHost user secrets, then restart:

   ```bash
   dotnet user-secrets set "Parameters:stripe-secret-key" "sk_test_..." --project src/AppHost/BuilderPro.AppHost
   dotnet user-secrets set "Parameters:stripe-webhook-secret" "whsec_..." --project src/AppHost/BuilderPro.AppHost
   ```

   With both set, the Quotes service uses Stripe; without them it stays on the simulated provider.
4. Pay with Stripe's test card 4242 4242 4242 4242. Card 4000 0000 0000 0002 is declined.

**Live keys (`sk_live_…`) are refused outside Production at startup.**

## Not yet

- Refunds (from Stripe, for example when the customer declines the measured total).
- The final balance payment.
- A staff screen for the payment history.
- Stripe Connect payouts to each store.
