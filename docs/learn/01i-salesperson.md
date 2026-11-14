# LEARN 1i: salesperson login (assisted selling)

**Owner decision:** a salesperson prepares the quote for a customer. The customer accepts it and pays the deposit through their own flow. The sale is credited to the salesperson for **commission**.

```
salesperson: find customer -> start project for them -> answers -> numbered quote
customer:    My projects ("prepared by Sam") -> Accept -> pay 5 % deposit
report:      paid deposit = a sale credited to Sam (commission at the store's rate)
```

| Piece | Where | Rule |
|---|---|---|
| Role | Identity membership scopes `sales` (salesperson) and `sales.report` (manager). Dev users: **sam** (sales) and **bob** (report, read-only). | Staff membership only (no customer id). |
| Find a customer | `GET /api/identity/customers?q=` (2–80 chars) | Searches the user store and keeps only **active customers of the caller's own store**. Staff aren't customers. |
| Customer check | `GET /api/identity/customers/{id}` | Quotes asks this, with the salesperson's token, before an on-behalf project. |
| Start for them | `POST /api/projects {zip, customerId, title}` | `sales` scope only (`sales_only`); unknown customer → 404; Identity down → 503. The project's customer is the customer; it records the salesperson (key and name). |
| Change it | answers and quote | The customer, or **the salesperson who prepared it**. Other staff → `not_your_customer`. |
| Accept and pay | unchanged | Customer only: the salesperson's Accept and deposit calls are refused. The workspace hides those buttons for staff. |
| Commission | `GET /api/sales/commission?from=&to=` (default: this month) | A sale = a **paid** deposit on a project the salesperson prepared. A salesperson sees their own line; `sales.report` sees everyone's. Rate: `Sales:CommissionPercent:{tenant}` or `:Default`. With no rate the report shows sales only. |

The web app has a **Sales** page in the avatar menu: customer search and the month's sales table. **Start project** opens the same ZIP → type → product steps the customer would take (`/quote/new?for=<customerId>`), using the customer's ZIP; choosing a product creates the project for them.

Not yet:
- inviting a new customer (sign-up link by email or text)
- discounts (an open owner question)
- a period picker on the page
