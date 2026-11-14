# LEARN 4b: AI phone scheduling (store option)

After the deposit, a customer can ask to be **called** to schedule the measure visit. Owner rules:
- It's a store package option.
- It needs its own automated-call consent first.
- Calls happen only within the customer's contact hours.
- The call says it's automated, offers 2–3 times and a person.
- **The customer confirms from a link; the call never books.**
- Every call offers an opt-out, and opting out stops all calls at once.

```
project (measure, no visit) --customer turns on "call me" (agrees to wording v)--> call scheduled
dispatcher: consent? still needed? inside contact hours? open times that fit?
   --> telephony: "automated assistant for <store>... press 1-3 for a time, 0 for a person, 9 to stop"
provider callback (signed) --1-3--> confirm link by email (+ text if allowed) --> customer confirms on the project page
                           --0----> person_requested (staff follow up)
                           --9----> consent withdrawn (source "call"), every open call cancelled
                           --no answer--> next day in their hours, up to 3 attempts
```

| Rule | Where |
|---|---|
| **Store option** | Package feature `phone-scheduling` (Acme has it in development). Without it → `feature_not_included`. |
| **Consent first** | `PUT /api/projects/{id}/automated-calls {consent, wordingVersion}`. Customer only, never an agent or staff. The wording version must be current (`stale_wording`). Separate from ordinary contact consent and append-only in `quotes.call_consents`. Snapshots phone, contact hours and message channels from the profile; no phone → 422. |
| **When it may call** | `CallWindows.MayCall`: inside contact hours AND 9:00–20:00 store time.<br>• business: Mon–Fri 9–17<br>• evenings: 17–20<br>• weekends: Sat–Sun 10–18<br>• anytime: 9–20<br>Otherwise the call waits for `NextAllowed`. |
| **What it offers** | Up to 3 open visit times that suit the hours, on different days (same rule as the 4a agent). None → cancelled with a note for staff. |
| **Never books** | A keypress sends a link `/projects/{id}?visit=…`. The project page shows **From your call: … Confirm this time**, and only the customer's click books. |
| **Opt-out** | Press 9, or turn it off on the project. The consent is recorded as withdrawn and every open call for the customer is cancelled immediately. |
| **Cancelled automatically** | When the visit gets booked, the project moves on, or consent is withdrawn (checked again right before dialling). |
| **Provider callback** | `POST /api/calls/{id}/events`, signed `X-BuilderPro-Signature: t=…,v1=HMAC(t.body)` with `Calls:WebhookSecret`, 5-minute window. Anonymous at the gateway; unsigned or replayed events change nothing. |

**Telephony** is behind `ITelephony`. "Simulated" records the call and its script; Azure Communication Services (Call Automation) or Twilio plug in there. Phone numbers are never logged.

**Tables:** `quotes.call_consents` and `quotes.calls` (schema v11), with one open call per project.

**Web:** the project's measure step has a **Prefer a call?** card (wording, **Yes, call me to schedule** / **Stop automated calls**, call status), and the confirm-link time appears at the top of the visit picker.

## Not yet

- A real telephony provider: ACS Call Automation with speech, or Twilio with gather.
- The customer's own time zone (today: the store's zone).
- A staff list of `person_requested` calls (comes with 3e).
- Speech understanding beyond keypresses.
