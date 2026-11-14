"""4a: the scheduling agent: graph decisions, A2A over HTTP, auth and on-behalf-of."""
import json
from datetime import datetime, timezone

import httpx
import pytest
from starlette.authentication import AuthCredentials, AuthenticationBackend, AuthenticationError
from starlette.testclient import TestClient

from app.auth import Caller, DelegationError, OnBehalfOf
from app.config import Settings
from app.graph import fits_contact_hours, propose
from app.main import create_app

PROJECT = "8a362998-d12a-454d-b986-b77197bb0d83"
SETTINGS = Settings(issuer="http://keycloak/realms/builderpro", token_url="http://keycloak/realms/builderpro/protocol/openid-connect/token",
                    client_secret="s3cret", allow_http_for_local_dev=True)


def slots(*starts_and_open):
    return [{"start": s, "available": a} for s, a in starts_and_open]


class FakePlatform:
    def __init__(self, stage="measure", hours="business", visit=None, open_slots=None):
        self.stage, self.hours, self.visit = stage, hours, visit
        self.slots = open_slots if open_slots is not None else slots(
            ("2026-11-14T15:00:00+00:00", True), ("2026-11-14T19:00:00+00:00", True),    # Sat 9:00 / 13:00 Central
            ("2026-11-16T15:00:00+00:00", False), ("2026-11-16T19:00:00+00:00", True),   # Mon (9:00 full)
            ("2026-11-17T15:00:00+00:00", True), ("2026-11-18T15:00:00+00:00", True),
            ("2026-11-19T15:00:00+00:00", True))
        self.calls = []

    async def project(self, project_id):
        self.calls.append(("project", project_id))
        return {"id": project_id, "stage": self.stage, "visit": self.visit}

    async def visit_slots(self, project_id):
        self.calls.append(("slots", project_id))
        return self.slots

    async def profile(self):
        self.calls.append(("profile",))
        return {"contactHours": self.hours}


# ---------- the graph ----------

def test_contact_hours_windows_in_store_time():
    sat_9 = datetime(2026, 11, 14, 15, tzinfo=timezone.utc)
    mon_9 = datetime(2026, 11, 16, 15, tzinfo=timezone.utc)
    mon_18 = datetime(2026, 11, 17, 0, tzinfo=timezone.utc)   # 18:00 Central on Monday
    assert fits_contact_hours(mon_9, "business") and not fits_contact_hours(sat_9, "business")
    assert fits_contact_hours(sat_9, "weekends") and not fits_contact_hours(mon_9, "weekends")
    assert fits_contact_hours(mon_18, "evenings") and not fits_contact_hours(mon_9, "evenings")
    assert fits_contact_hours(sat_9, "anytime")


async def test_proposes_up_to_three_open_times_on_different_weekdays_for_business_hours():
    result = await propose(FakePlatform(hours="business"), PROJECT)
    assert result["outcome"] == "proposed"
    assert [p["start"] for p in result["proposals"]] == ["2026-11-16T19:00:00+00:00", "2026-11-17T15:00:00+00:00", "2026-11-18T15:00:00+00:00"]
    assert "nothing is booked until you do" in result["message"]


async def test_weekend_customer_and_no_fit_and_not_ready_and_already_booked():
    assert [p["start"] for p in (await propose(FakePlatform(hours="weekends"), PROJECT))["proposals"]] == ["2026-11-14T15:00:00+00:00"]
    assert (await propose(FakePlatform(hours="evenings"), PROJECT))["outcome"] == "no_fit"
    not_ready = FakePlatform(stage="deposit")
    assert (await propose(not_ready, PROJECT))["outcome"] == "not_ready"
    assert ("slots", PROJECT) not in not_ready.calls
    booked = await propose(FakePlatform(visit={"status": "booked", "start": "2026-11-16T15:00:00+00:00"}), PROJECT)
    assert booked["outcome"] == "booked"


# ---------- on-behalf-of ----------

def _idp(answer: httpx.Response, seen: list):
    def handler(request: httpx.Request):
        seen.append(dict(x.split("=", 1) for x in request.content.decode().split("&")))
        return answer
    return httpx.AsyncClient(transport=httpx.MockTransport(handler))


async def test_on_behalf_of_exchanges_and_checks_the_answer():
    seen = []
    obo = OnBehalfOf(SETTINGS, _idp(httpx.Response(200, json={"access_token": "platform", "token_type": "Bearer", "expires_in": 300}), seen))
    assert await obo.platform_token("caller") == "platform"
    assert await obo.platform_token("caller") == "platform"
    assert len(seen) == 1   # cached per caller token
    assert seen[0]["grant_type"] == "urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Atoken-exchange"
    assert seen[0]["audience"] == "builderpro-api"
    for bad in (httpx.Response(400, json={}), httpx.Response(200, json={"access_token": "t", "token_type": "mac", "expires_in": 300}),
                httpx.Response(200, json={"access_token": "t", "token_type": "Bearer", "expires_in": 0}), httpx.Response(200, text="nope")):
        with pytest.raises(DelegationError):
            await OnBehalfOf(SETTINGS, _idp(bad, [])).platform_token("caller")


def test_settings_refuse_plain_http_to_real_hosts_and_a_missing_secret():
    with pytest.raises(ValueError):
        Settings(issuer="http://idp.example.com/realms/x", token_url="https://idp.example.com/t", client_secret="s")
    with pytest.raises(ValueError):
        Settings(issuer="https://idp.example.com/realms/x", token_url="https://idp.example.com/t", client_secret="")


# ---------- A2A over HTTP ----------

class FakeAuth(AuthenticationBackend):
    """Stands in for JWT validation: 'Bearer good' is alice."""

    async def authenticate(self, conn):
        header = conn.headers.get("authorization")
        if header is None:
            return None
        if header != "Bearer good":
            raise AuthenticationError("invalid token")
        return AuthCredentials(["agent:call"]), Caller(subject="alice-sub", token="good")


def _client(platform: FakePlatform, exchanged: list):
    def idp(request: httpx.Request):
        exchanged.append(request.content.decode())
        return httpx.Response(200, json={"access_token": "platform-token", "token_type": "Bearer", "expires_in": 300})
    tokens = []
    app = create_app(SETTINGS, http=httpx.AsyncClient(transport=httpx.MockTransport(idp)), auth=FakeAuth(),
                     platform_factory=lambda token: (tokens.append(token), platform)[1])
    return TestClient(app), tokens


def _send(client, data, token="good"):
    body = {"jsonrpc": "2.0", "id": "1", "method": "SendMessage",
            "params": {"message": {"messageId": "m1", "role": "ROLE_USER", "parts": [{"data": data}]}}}
    headers = {"A2A-Version": "1.0", "Content-Type": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    return client.post("/a2a", content=json.dumps(body), headers=headers)


def test_the_agent_card_is_public_and_describes_the_skill():
    client, _ = _client(FakePlatform(), [])
    card = client.get("/.well-known/agent-card.json").json()
    assert card["name"] == "BuilderPro scheduling agent"
    assert card["skills"][0]["id"] == "propose_visit_times"
    assert card["supportedInterfaces"][0]["url"].endswith("/a2a")


def test_a2a_call_proposes_times_on_behalf_of_the_caller():
    exchanged = []
    client, tokens = _client(FakePlatform(hours="business"), exchanged)
    response = _send(client, {"projectId": PROJECT})
    assert response.status_code == 200, response.text
    task = response.json()["result"]["task"]
    assert task["status"]["state"] == "TASK_STATE_COMPLETED"
    data = next(p["data"] for p in task["artifacts"][0]["parts"] if "data" in p)
    assert data["outcome"] == "proposed" and len(data["proposals"]) == 3
    assert tokens == ["platform-token"]          # the platform got the EXCHANGED token
    assert "subject_token=good" in exchanged[0]  # exchanged from the caller's token, never forwarded


def test_a2a_requires_a_valid_token_and_a_project_id():
    client, _ = _client(FakePlatform(), [])
    assert _send(client, {"projectId": PROJECT}, token=None).status_code == 401
    assert _send(client, {"projectId": PROJECT}, token="forged").status_code == 401
    rejected = _send(client, {"nothing": 1}).json()["result"]["task"]
    assert rejected["status"]["state"] == "TASK_STATE_REJECTED"
