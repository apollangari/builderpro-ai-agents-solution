"""The scheduling agent's reasoning, as a LangGraph graph.

    load_context -> choose_slots -> respond

It never books anything: it PROPOSES measure-visit times that fit the customer's contact hours. The
customer confirms a time themselves (in the app, or from a link), so a person always makes the booking.
The steps are deterministic on purpose: times and dates come from the platform, never from a model.
"""
from datetime import datetime
from typing import Any, Protocol, TypedDict
from zoneinfo import ZoneInfo

from langgraph.graph import END, START, StateGraph

STORE_ZONE = ZoneInfo("America/Chicago")
MAX_PROPOSALS = 3


class Platform(Protocol):
    async def project(self, project_id: str) -> dict: ...
    async def visit_slots(self, project_id: str) -> list[dict]: ...
    async def profile(self) -> dict: ...


class SchedulingState(TypedDict, total=False):
    project_id: str
    stage: str
    contact_hours: str
    slots: list[dict]
    proposals: list[dict]
    outcome: str        # proposed | no_fit | not_ready | booked
    message: str


def fits_contact_hours(start: datetime, hours: str) -> bool:
    """business 08-17 Mon-Fri; evenings 17-21; weekends Sat/Sun; anytime. In the store's zone."""
    local = start.astimezone(STORE_ZONE)
    weekday = local.weekday() < 5
    match hours:
        case "anytime":
            return True
        case "weekends":
            return not weekday
        case "evenings":
            return 17 <= local.hour < 21
        case _:  # business is the default
            return weekday and 8 <= local.hour < 17


def build_graph(platform: Platform):
    async def load_context(state: SchedulingState) -> SchedulingState:
        project = await platform.project(state["project_id"])
        stage = project.get("stage", "")
        visit = project.get("visit") or {}
        if visit.get("status") == "booked":
            return {"stage": stage, "outcome": "booked", "slots": [], "contact_hours": "",
                    "proposals": [{"start": visit["start"]}]}
        if stage != "measure":
            return {"stage": stage, "outcome": "not_ready", "slots": [], "contact_hours": ""}
        profile = await platform.profile()
        return {"stage": stage, "slots": await platform.visit_slots(state["project_id"]),
                "contact_hours": profile.get("contactHours") or "business"}

    async def choose_slots(state: SchedulingState) -> SchedulingState:
        if state.get("outcome") in ("not_ready", "booked"):
            return {}
        open_slots = [s for s in state.get("slots", []) if s.get("available")]
        fitting = [s for s in open_slots if fits_contact_hours(datetime.fromisoformat(s["start"]), state["contact_hours"])]
        # Spread the proposals over different days so the customer has a real choice.
        chosen, days = [], set()
        for s in fitting:
            day = datetime.fromisoformat(s["start"]).astimezone(STORE_ZONE).date()
            if day not in days:
                chosen.append({"start": s["start"]})
                days.add(day)
            if len(chosen) == MAX_PROPOSALS:
                break
        return {"proposals": chosen, "outcome": "proposed" if chosen else "no_fit"}

    async def respond(state: SchedulingState) -> SchedulingState:
        def say(s: dict) -> str:
            return datetime.fromisoformat(s["start"]).astimezone(STORE_ZONE).strftime("%A %b %d, %I:%M %p").replace(" 0", " ")
        match state.get("outcome"):
            case "booked":
                text = f"Your measure visit is already booked for {say(state['proposals'][0])} (store time)."
            case "not_ready":
                text = "The measure visit is scheduled after the deposit is paid."
            case "no_fit":
                text = ("None of the open visit times fall inside your contact hours. "
                        "Pick any time on your project page, or ask for a person to call you.")
            case _:
                text = ("These measure-visit times fit your contact hours: "
                        + "; ".join(say(s) for s in state["proposals"])
                        + " (store time). Confirm one on your project page; nothing is booked until you do.")
        return {"message": text}

    graph = StateGraph(SchedulingState)
    graph.add_node("load_context", load_context)
    graph.add_node("choose_slots", choose_slots)
    graph.add_node("respond", respond)
    graph.add_edge(START, "load_context")
    graph.add_edge("load_context", "choose_slots")
    graph.add_edge("choose_slots", "respond")
    graph.add_edge("respond", END)
    return graph.compile()


async def propose(platform: Platform, project_id: str) -> dict[str, Any]:
    result = await build_graph(platform).ainvoke({"project_id": project_id})
    return {"outcome": result["outcome"], "proposals": result.get("proposals", []), "message": result["message"]}
