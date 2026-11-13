# LEARN 2f: Redis for hot reads

**Time:** about 30 minutes · **Goal:** add a cache without giving up correctness: what to cache,
who invalidates it, and what happens when Redis is down.

## 1. What's cached, and why those two

| Read | How often | Cached as | TTL | Invalidated by |
|---|---|---|---|---|
| "Which tenant is this caller in?" (Catalog, Quotes → Identity) | **every** catalog/quote request | `bp:tenant:{userKey}\|{hint}` | 60 s | Identity, on membership upsert/revoke (and dev seed) |
| "All active products of tenant T" | every catalog view and every quote | `bp:catalog:products:{tenant}` | 5 min | Catalog, on product upsert |

Everything else is still read straight from Postgres. This is "CQRS-lite": reads and writes take
different paths, but Postgres stays the only source of truth.

## 2. The rules

1. **Cache-aside.** Read the cache; on a miss read the source and fill the cache.
2. **Write the source first, then delete the cache entry.** Never update the cache in place: the
   next read rebuilds it from the truth.
3. **Only cache success.** A `403 no_membership` is never cached, so a user who just got a
   membership doesn't wait for a TTL.
4. **The owner of the data invalidates.** Identity owns memberships, so Identity deletes the
   tenant-lookup keys, using the same `TenantCacheKeys` code the readers use.
5. **The cache is optional.** No `cache` connection string → no caching at all (not an in-process
   fallback: Identity couldn't invalidate another process's memory). Redis errors → log and read
   the source.

## 3. Why revocation is still immediate

A membership (user U, tenant T) can only have produced cached answers for hint "" and hint T.
On revoke, Identity deletes exactly those two keys, so the next request asks Identity again. The
60 s TTL only bounds one rare race: a lookup already in flight when the revoke happens.

## 4. Try it

1. Run the AppHost: there's a `cache` (Redis) resource. Open the catalog twice and look at the
   traces: the second request has no call to `identity` and no SQL.
2. Revoke a membership with the Identity admin API, refresh the catalog: refused immediately.
3. Stop `cache` in the dashboard: the catalog still works (slower; warnings in the logs).

## 5. Not yet
- Per-tenant cache metrics, HybridCache (L1 in-process + L2 Redis with tags), and stampede
  protection. Add them when traces show they're needed, not before.
