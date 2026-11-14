# LEARN 3c: BuilderPro tools over MCP, with on-behalf-of auth

Any MCP client (Claude Desktop, VS Code, another agent) can use BuilderPro's catalog and quote tools **as the signed-in customer**.

## The endpoint

`POST /api/assistant/mcp`: Streamable HTTP, stateless, built with the official C# SDK (`ModelContextProtocol.AspNetCore`).

| Tool | What it does |
|---|---|
| `check_zip`, `list_project_types`, `find_products`, `get_project_steps` | Read the catalog for a ZIP, at that ZIP's prices |
| `price_draft` | Prices a draft with the store's pricing engine; saves nothing |
| `list_my_projects`, `get_project` | My projects, quotes, deposit, visit and install progress |

- **No commit tools.** There is nothing that accepts, pays, books or saves; those stay the customer's own clicks.
- **Agent marker.** Every downstream call carries `X-BuilderPro-Agent`, so commit endpoints refuse it anyway.
- **Same code as the concierge.** The tools are the concierge's own, so there is one behaviour behind two front doors.

## On-behalf-of: no token passthrough

```
MCP client / web app --(token aud: builderpro-assistant)--> Assistant
Assistant --(RFC 8693 token exchange, client builderpro-assistant)--> Keycloak
Keycloak --(new token, same user, aud: builderpro-api)--> Assistant --> Catalog / Quotes
```

- **Audience.** The Assistant accepts only tokens issued **for it** (`Auth:Audience = builderpro-assistant`).
- **Exchange.** Before any tool call it exchanges the caller's token for a platform-API token for the same person. The subject stays the same, so tenant, customer and scopes are unchanged.
- **No passthrough.** The caller's token never leaves the Assistant. MCP's security guidance forbids passing it through.
- **Protocol.** Set explicitly with `Assistant:TokenExchange:Protocol`:
  - `token-exchange` (RFC 8693: Keycloak, Okta, Auth0)
  - `entra-obo` (Entra `jwt-bearer` with `requested_token_use=on_behalf_of` and a `.default` scope), for Azure in 5a
- **Hardening:**
  - Settings and endpoint are validated at startup. HTTPS is required, except a local identity provider in development.
  - A dedicated HTTP client: no caller credentials, no redirects, 10 s timeout.
  - The answer must say `Bearer`, have a positive `expires_in`, a sane size and no whitespace.
  - One exchange per caller token, cached until 30 s before expiry, keyed by a SHA-256 of the token.
  - Failures are sanitised: no tokens or response bodies in logs or errors. A refused exchange gives the model `{error: 401}`.
- **Realm clients:**
  - `builderpro-assistant`: confidential, standard token exchange on, adds the `builderpro-api` audience.
  - `builderpro-mcp`: public, authorization code + PKCE on loopback redirects, tokens for the Assistant.
  - `builderpro-web` tokens now include `builderpro-assistant` as well.

## Discovery (RFC 9728)

- A 401 from `/api/assistant/mcp` carries `WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource/api/assistant/mcp"`.
- That document names Keycloak as the authorization server, so a client can sign the customer in with `builderpro-mcp`.
- The gateway lets `/api/assistant/mcp` and `/.well-known/oauth-protected-resource/**` through unauthenticated so the challenge reaches the client. The Assistant still requires its token.

## Live-tested

- Alice's web token now has `aud: [builderpro-assistant, builderpro-api]`.
- `tools/call list_my_projects` returned her projects through the token exchange.
- An anonymous call gets the 401 with `resource_metadata`.

## Not yet

- Dynamic client registration for arbitrary MCP clients (they use `builderpro-mcp`).
- Per-tool consent prompts.
