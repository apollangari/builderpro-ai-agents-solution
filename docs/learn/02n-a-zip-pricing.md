# LEARN 2n-a: prices by ZIP code

**Time:** about 30 minutes · **Goal:** let the same product cost different amounts in different
areas, without ever trusting the browser with a price or showing a store's service area.

## 1. The model

| Table (catalog schema v3) | Key | Meaning |
|---|---|---|
| `price_zones` | (tenant, zone) | A named area of one store, e.g. "Chicago" |
| `zone_zips` | (tenant, **zip**) | Which zone a ZIP is in. The key makes "one zone per ZIP per store" a database rule |
| `zone_prices` | (tenant, zone, sku) | The product's price in that zone. **No row = not sold there** |

The product's `list_price` is still there: staff tools (the 2m quote builder) price without a ZIP.

## 2. The flow

```
/quote/new ── "Is this project at ZIP 62701?" (postal_code claim from sign-up)
     │ Yes / or the ZIP the customer types
     ▼
GET /api/catalog/service-area?zip=62701   ->  { zip: "62701", served: true }   (yes/no only)
     ▼
GET /api/catalog/products?zip=62701       ->  only products with a price in that zone, at that price
     ▼
project types = categories that have products here  ->  products  ->  (2n-b: measurements)
```

Quotes and estimates take the same `zip` and ask the catalog with it, so the price on the card and
the price on the quote come from the same row.

## 3. The rules

1. **ZIPs are normalized on the server**: `75201-1234` -> `75201`, `k1a0b1` -> `K1A 0B1`; anything
   else is `400 invalid_zip`.
2. **An unserved ZIP is named, not empty**: `422 zip_not_served`, and the page says "We don't serve
   ZIP 90210 yet". An empty list would look like a broken store.
3. **The service area never leaves the server**: no zone ids, names or ZIP lists in any response.
   Another store can't map your coverage by reading your API.
4. **Zones are per store**: Acme's zones mean nothing for Globex, even for the same ZIP.
5. **A new ZIP isn't saved to the profile**: the customer may be quoting a rental or a parent's house.

## 4. Try it (dev seed)

Seeded users live at ZIP 62701 ("Springfield", list prices). Acme also serves Chicago (60601-60607)
at list price x 1.12, where butcher block and the wood shed aren't sold. Compare:

```
GET /api/catalog/products?zip=62701&category=countertops
GET /api/catalog/products?zip=60601&category=countertops
GET /api/catalog/service-area?zip=90210
```

## 5. Not yet

- The ZIP isn't stored on a saved quote yet (2n-e stores it with the measurements and choices).
- The assistant (3a) still prices with list prices; the concierge (3b) will ask for the ZIP first.
- Zone reads aren't cached; both are single indexed lookups. Cache them with the 2f rules if needed.
