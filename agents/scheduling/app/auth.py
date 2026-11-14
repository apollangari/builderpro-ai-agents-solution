"""Who is calling, and on-behalf-of for our own calls.

Inbound: only a valid token issued FOR this agent (issuer, audience, signature, expiry) is accepted.
Outbound: the caller's token is exchanged (RFC 8693) for a platform-API token for the SAME person; the
caller's token is never forwarded. Failures are sanitised: no tokens or response bodies in errors/logs.
"""
from dataclasses import dataclass
import hashlib
import logging
import time

import httpx
import jwt
from starlette.authentication import AuthCredentials, AuthenticationBackend, AuthenticationError, BaseUser
from starlette.requests import HTTPConnection

from .config import Settings

log = logging.getLogger(__name__)
MAX_TOKEN = 65_536


class DelegationError(Exception):
    """The identity provider refused or failed the on-behalf-of exchange (safe to show)."""


@dataclass
class Caller(BaseUser):
    subject: str
    token: str          # kept in memory for the exchange only; never logged or forwarded

    @property
    def is_authenticated(self) -> bool:
        return True

    @property
    def display_name(self) -> str:
        return self.subject


def _token_shaped(token: object) -> bool:
    return isinstance(token, str) and 0 < len(token) <= MAX_TOKEN and not any(c.isspace() for c in token)


class BearerAuth(AuthenticationBackend):
    """Validates the bearer token against the identity provider's published keys."""

    def __init__(self, settings: Settings, jwks: jwt.PyJWKClient | None = None):
        self._settings = settings
        self._jwks = jwks or jwt.PyJWKClient(settings.jwks_url, cache_keys=True, lifespan=600)

    async def authenticate(self, conn: HTTPConnection):
        header = conn.headers.get("authorization", "")
        if not header.lower().startswith("bearer "):
            return None
        token = header[7:].strip()
        if not _token_shaped(token):
            raise AuthenticationError("invalid token")
        try:
            key = self._jwks.get_signing_key_from_jwt(token).key
            claims = jwt.decode(token, key, algorithms=["RS256", "ES256"], audience=self._settings.audience,
                                issuer=self._settings.issuer, options={"require": ["exp", "iss", "sub", "aud"]}, leeway=60)
        except (jwt.PyJWTError, jwt.PyJWKClientError):
            raise AuthenticationError("invalid token") from None
        return AuthCredentials(["agent:call"]), Caller(subject=claims["sub"], token=token)


class OnBehalfOf:
    """RFC 8693 token exchange, one per caller token, cached until shortly before it expires."""

    def __init__(self, settings: Settings, http: httpx.AsyncClient):
        self._settings = settings
        self._http = http
        self._cache: dict[str, tuple[str, float]] = {}

    async def platform_token(self, caller_token: str) -> str:
        if not _token_shaped(caller_token):
            raise DelegationError("invalid caller token")
        key = hashlib.sha256(caller_token.encode()).hexdigest()
        hit = self._cache.get(key)
        if hit and hit[1] > time.monotonic():
            return hit[0]
        try:
            response = await self._http.post(self._settings.token_url, data={
                "grant_type": "urn:ietf:params:oauth:grant-type:token-exchange",
                "client_id": self._settings.client_id,
                "client_secret": self._settings.client_secret,
                "subject_token": caller_token,
                "subject_token_type": "urn:ietf:params:oauth:token-type:access_token",
                "requested_token_type": "urn:ietf:params:oauth:token-type:access_token",
                "audience": self._settings.platform_audience,
            }, follow_redirects=False, timeout=10.0)
            if response.status_code != 200:
                log.warning("on-behalf-of exchange refused (%s)", response.status_code)
                raise DelegationError("delegated authentication unavailable")
            payload = response.json()
        except (httpx.HTTPError, ValueError):
            raise DelegationError("delegated authentication unavailable") from None
        token = payload.get("access_token") if isinstance(payload, dict) else None
        expires = payload.get("expires_in") if isinstance(payload, dict) else None
        if (not _token_shaped(token) or str(payload.get("token_type", "")).lower() != "bearer"
                or type(expires) is not int or expires <= 0):
            raise DelegationError("invalid delegated authentication response")
        if len(self._cache) > 10_000:
            self._cache.clear()
        self._cache[key] = (token, time.monotonic() + max(0, expires - 30))
        return token
