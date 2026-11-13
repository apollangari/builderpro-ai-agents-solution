# Email (sign-up verification, password reset)

Keycloak sends the account emails. Where they go depends on configuration, never on code.

| Where | Sender | How it's chosen |
|---|---|---|
| Local, default | **Mailpit** (AppHost resource `mailpit`, inbox at http://localhost:8025) | Always runs; catches every email, sends nothing to the internet |
| Local, opt-in | **Zoho** (`builderpro.everisys.com`) | Only when the `smtp-*` user secrets below exist |
| Azure (later) | Zoho or Azure Communication Services | Same `SMTP_*` settings from Key Vault |

The realm file reads `SMTP_HOST`, `SMTP_PORT`, `SMTP_STARTTLS`, `SMTP_SSL`, `SMTP_AUTH`, `SMTP_USER`,
`SMTP_PASSWORD`, `SMTP_FROM` at import time; defaults point at Mailpit.

## Use Zoho locally (optional)

1. In Zoho Mail, create (or pick) the sending mailbox, e.g. `no-reply@builderpro.everisys.com`.
2. Zoho → My Account → Security → **App Passwords** → generate one for "BuilderPro Keycloak".
   Use this, never the account password (and it keeps working with 2FA on).
3. Store the settings (values stay on your machine, never in git):

```bash
dotnet user-secrets set "Parameters:smtp-host" "smtp.zoho.com" --project src/AppHost/BuilderPro.AppHost
dotnet user-secrets set "Parameters:smtp-port" "587" --project src/AppHost/BuilderPro.AppHost
dotnet user-secrets set "Parameters:smtp-user" "no-reply@builderpro.everisys.com" --project src/AppHost/BuilderPro.AppHost
dotnet user-secrets set "Parameters:smtp-password" "<zoho app password>" --project src/AppHost/BuilderPro.AppHost
```

   Use your Zoho data-centre host: `smtp.zoho.com` (US), `smtp.zoho.eu`, `smtp.zoho.in`, ... Organisation
   (paid) accounts may need `smtppro.zoho.com` / `smtppro.zoho.eu`. Check Zoho Mail → Settings → Mail
   Accounts → SMTP.
4. Restart the AppHost (really stop the old one first). Keycloak sends with STARTTLS on 587.

Remove the `smtp-*` secrets to go back to Mailpit.

## DNS in Cloudflare (deliverability)

Without these, verification emails land in spam. Zoho Mail → Admin Console → Domains →
`builderpro.everisys.com` → Email Configuration shows the exact values for your account:

| Record | Purpose |
|---|---|
| **MX** (from Zoho) | Receive mail for the domain |
| **TXT SPF**: `v=spf1 include:zoho.com ~all` (Zoho shows the exact include for your region) | Allows Zoho to send for the domain. One SPF record per name only: merge if one exists |
| **TXT DKIM** (selector + key from Zoho) | Signs outgoing mail |
| **TXT DMARC** at `_dmarc.builderpro.everisys.com`: start with `v=DMARC1; p=none; rua=mailto:<you>` | Reports first, tighten to `quarantine` later |

In Cloudflare these are **DNS only** (grey cloud); mail records can't be proxied.

## Test it

Sign up at http://localhost:4200/signup?store=acme with an address you can read. Mailpit: open
http://localhost:8025. Zoho: check that inbox (and spam the first time).
