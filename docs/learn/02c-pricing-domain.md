# LEARN 2c: Pricing as pure domain code

**Time:** about 30 minutes · **Goal:** see why money rules live in one small, pure, heavily
tested library, and why that's the foundation that makes AI agents safe to add later.

## 1. What changed

| File | What it does |
|---|---|
| `src/Services/Quotes/BuilderPro.Quotes.Domain/Money.cs` | `Money` (decimal + ISO currency) and the ONE `Rounding` rule |
| `src/Services/Quotes/BuilderPro.Quotes.Domain/QuotePricer.cs` | `QuotePricer.Price(input)`: validate, then price |
| `tests/BuilderPro.Quotes.Domain.Tests/` | 33 unit tests: totals, rounding, invalid input |

No HTTP, no database, no NuGet packages, no clock. The Quote API (2d) wraps it.

## 2. The rules (read them in `QuotePricer.cs`)

1. A quote has 1–100 lines; each SKU at most once.
2. Quantity > 0, ≤ 100 000, at most 3 decimals (m², metres, litres).
3. Unit prices ≥ 0, one currency per quote.
4. **Line total = quantity × unit price, rounded per line.** What's printed is what's summed.
5. Subtotal = sum of the rounded line totals.
6. **Tax = subtotal × rate, rounded once.**
7. Total = subtotal + tax.

## 3. Concepts

### decimal, never double
`0.1 + 0.2` is `0.30000000000000004` in `double`. In `decimal` it's `0.3`. Money is base-10.

### One rounding rule, one place
.NET's `Math.Round` defaults to **banker's rounding** (0.125 → 0.12). Customers expect
**half away from zero** (0.125 → 0.13). `Rounding.ToMinorUnit` is the only place that rounds;
two different rules in two places is how a screen and an invoice end up a cent apart.

### Where you round changes the answer
Three lines of ⅓ unit at 1.00: round each line (0.33 + 0.33 + 0.33 = **0.99**) vs round the sum
(**1.00**). Rule 4 picks the first so the printed lines add up. Tax goes the other way (rule 6):
rounding tax per line on ten 0.05 items gives 0.10, rounding once gives 0.05. Both are tested.

### Report every problem
`Price` returns `PricingResult` with **all** errors (stable `code` + line index), not an exception
on the first one. An agent or a form can fix everything in one round trip.

### Why this matters for agents
The roadmap's one rule: *agents decide; the domain enforces.* An agent will propose lines
("3 cans of paint, 12.5 m² of tile"); prices come from the catalog and totals from this code.
An LLM never multiplies, rounds or adds money. Because `Price` is pure, the same proposal
always produces the same total, which you can audit.

## 4. Labs

1. Change `MidpointRounding.AwayFromZero` to `ToEven`. Which tests fail? Put it back.
2. Move the rounding from each line to the subtotal. Which test explains why that's wrong here?
3. Add a rule: maximum 10 % discount. Write the test first, as a new `PricingError` code.

## 5. Not yet
- Discounts, per-currency minor units (JPY, BHD), tax by region. Each is a new rule + tests here.
- Persistence and an API: that's 2d.
