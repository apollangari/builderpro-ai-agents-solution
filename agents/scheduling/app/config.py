"""Settings from the environment (Aspire passes them; nothing secret has a default outside development)."""
from dataclasses import dataclass, field
import os
from urllib.parse import urlsplit


@dataclass(frozen=True)
class Settings:
    # Who may call this agent: tokens issued FOR it by our identity provider.
    issuer: str
    audience: str = "builderpro-scheduling"
    jwks_url: str = ""
    # On-behalf-of: exchange the caller's token for a platform-API token for the same person.
    token_url: str = ""
    client_id: str = "builderpro-scheduling"
    client_secret: str = field(default="", repr=False)
    platform_audience: str = "builderpro-api"
    platform_base_url: str = "http://gateway"
    public_url: str = "http://localhost:8000"
    allow_http_for_local_dev: bool = False

    def __post_init__(self):
        for name, url in (("issuer", self.issuer), ("token_url", self.token_url)):
            parts = urlsplit(url)
            if not parts.scheme or not parts.hostname or parts.username or parts.password or parts.query or parts.fragment:
                raise ValueError(f"{name} must be a plain absolute URL")
            local = parts.hostname in ("localhost", "127.0.0.1") or "." not in parts.hostname
            if parts.scheme != "https" and not (self.allow_http_for_local_dev and local):
                raise ValueError(f"{name} must be HTTPS")
        if not self.client_secret.strip():
            raise ValueError("SCHEDULING_CLIENT_SECRET is required (on-behalf-of exchange)")

    @staticmethod
    def from_env() -> "Settings":
        issuer = os.environ["AUTH_AUTHORITY"].rstrip("/")
        return Settings(
            issuer=issuer,
            audience=os.environ.get("AUTH_AUDIENCE", "builderpro-scheduling"),
            jwks_url=os.environ.get("AUTH_JWKS_URL", f"{issuer}/protocol/openid-connect/certs"),
            token_url=os.environ.get("TOKEN_EXCHANGE_URL", f"{issuer}/protocol/openid-connect/token"),
            client_id=os.environ.get("SCHEDULING_CLIENT_ID", "builderpro-scheduling"),
            client_secret=os.environ.get("SCHEDULING_CLIENT_SECRET", ""),
            platform_base_url=os.environ.get("PLATFORM_BASE_URL", "http://gateway").rstrip("/"),
            public_url=os.environ.get("PUBLIC_URL", "http://localhost:8000").rstrip("/"),
            allow_http_for_local_dev=os.environ.get("ENVIRONMENT", "Development") != "Production",
        )
