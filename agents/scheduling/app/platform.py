"""BuilderPro's platform API (through the gateway) with an on-behalf-of token for the caller."""
import re

import httpx

AGENT_HEADER = "X-BuilderPro-Agent"   # commit actions (accept, pay, book) refuse calls marked as an agent's
_ID = re.compile(r"^[0-9a-fA-F-]{36}$")


class PlatformError(Exception):
    def __init__(self, status: int):
        super().__init__(f"platform answered {status}")
        self.status = status


class HttpPlatform:
    def __init__(self, http: httpx.AsyncClient, base_url: str, token: str):
        self._http = http
        self._base = base_url
        self._headers = {"Authorization": f"Bearer {token}", AGENT_HEADER: "scheduling"}

    async def _get(self, path: str):
        response = await self._http.get(f"{self._base}{path}", headers=self._headers, timeout=15.0, follow_redirects=False)
        if response.status_code != 200:
            raise PlatformError(response.status_code)
        return response.json()

    async def project(self, project_id: str) -> dict:
        if not _ID.match(project_id):
            raise PlatformError(400)
        return await self._get(f"/api/projects/{project_id}")

    async def visit_slots(self, project_id: str) -> list[dict]:
        return await self._get(f"/api/projects/{project_id}/visit/slots")

    async def profile(self) -> dict:
        return await self._get("/api/identity/profile")
