# LEARN 6c: installation tracking

After the measured quote is the order (6b), store staff move the job through four steps. The customer follows them in the workspace.

```
scheduled (date; may be moved) -> fabricating -> installing -> done (project closed)
```

| Piece | Endpoint | Rule |
|---|---|---|
| Next step | `POST /api/projects/{id}/install {step, scheduledFor?, note?}` | Staff with `scheduling.write` only; never the customer or an agent. Stage `install` only. Steps go one at a time (`invalid_transition`). `scheduled` needs a future date and can be repeated to move it until fabrication starts. `done` moves the project to `done`. |
| Order updates | written with the step (same transaction) to `quotes.notifications` | Only on the channels the customer **consented** to. Identity reads their **latest** consent (`GET /api/identity/customers/{id}/channels`): email always, `sms` for text or both, `call` for call or both. If Identity can't answer, only email is recorded (fail closed). Messages carry the project title and dates, no prices. |
| Read updates | `GET /api/projects/{id}/notifications` | The customer sees their own; staff see their store's. |

Tables: `quotes.install_events` and `quotes.notifications` (schema v8, with `sent_at` for the sender).

Web: the workspace shows the order confirmation, the install date and a four-step strip (done, current, next). The journey tracker moves to Install.

Not yet:
- ~~the sender~~: done (NotificationSender: email via SMTP/Mailpit any time, texts within contact hours)
- ~~a staff screen for the steps~~: done (store desk, Work tab)
- ~~the final balance payment~~: done (Pay balance when the job is done)
