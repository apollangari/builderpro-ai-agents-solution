# LEARN 6b: the measure visit

Once the deposit is paid (6a), the price per unit is held. The measure visit confirms the sizes on site.

```
measure: book a visit time -> staff record the measured sizes -> re-priced at the HELD unit prices
   same total    -> accepted for the customer -> install
   changed total -> confirm: the customer accepts the measured quote -> install
```

| Step | Endpoint | Rule |
|---|---|---|
| Times | `GET /api/projects/{id}/visit/slots` | Stage `measure` only. The store's times (`Visits:{tenant}:Times`, default 09:00 and 13:00) in its zone (`TimeZone`, default America/Chicago). Starts tomorrow and runs `Days` days (14). Closed days are skipped (Sunday). `Visits:PerSlot` bookings per time (2). |
| Book / move | `POST /api/projects/{id}/visit {start}` | The customer, or staff with `scheduling.write`. Must be an offered time with room (`slot_full` otherwise, decided under an advisory lock). Booking again moves the visit. |
| Measured | `POST /api/projects/{id}/visit/measured {measurements}` | Staff with `scheduling.write` only; never the customer or an agent. Needs a booked visit and the paid deposit. Re-prices with the measured quantities at the held unit prices and held tax rate (`HeldPrices.Apply`); a new line keeps today's price. Issues a new numbered quote. |
| Confirm | `POST /api/projects/{id}/confirm` | Customer only (never staff or an agent), stage `confirm`, before the measured quote expires. Accept is refused once the deposit is paid. |

Dev user **mia** (Acme, `scheduling.write`) records visits and install steps.

Tables: `quotes.visits` (schema v7), with one active visit per project. The project's paid deposit carries over to the measured quote.

Web: the workspace shows the times grouped by day, the booked visit with **Change time**, and **Confirm new total** for a changed measured quote.

Not yet:
- a staff screen to enter the measured sizes (API only; it comes with 3e's staff console)
- what happens if the customer declines the new total (deposit refund)
- offering only times inside the customer's contact hours
- reminders before the visit
- per-measurer calendars
