"""The scheduling agent over A2A (protocol 1.0): its card, and the executor that runs the graph."""
import logging

import httpx
from a2a.helpers.proto_helpers import get_data_parts, new_data_part, new_task_from_user_message, new_text_message, new_text_part
from a2a.server.agent_execution import AgentExecutor, RequestContext
from a2a.server.events import EventQueue
from a2a.server.tasks import TaskUpdater
from a2a.types import AgentCapabilities, AgentCard, AgentInterface, AgentSkill, Role

from .auth import Caller, DelegationError, OnBehalfOf
from .config import Settings
from .graph import propose
from .platform import HttpPlatform, PlatformError

log = logging.getLogger(__name__)
SKILL = "propose_visit_times"


def agent_card(settings: Settings) -> AgentCard:
    return AgentCard(
        name="BuilderPro scheduling agent",
        description="Proposes measure-visit times for a customer's project that fit their contact hours. "
                    "It never books: the customer confirms a time themselves.",
        version="1.0.0",
        supported_interfaces=[AgentInterface(url=f"{settings.public_url}/a2a", protocol_binding="JSONRPC", protocol_version="1.0")],
        capabilities=AgentCapabilities(streaming=False),
        default_input_modes=["application/json"],
        default_output_modes=["application/json", "text/plain"],
        skills=[AgentSkill(
            id=SKILL, name="Propose measure-visit times",
            description="Input: a data part {\"projectId\": \"...\"}. Output: up to 3 times within the customer's contact hours, "
                        "on different days, plus a short message. Requires a bearer token issued for this agent.",
            tags=["scheduling", "measure-visit"], examples=["{\"projectId\": \"8a362998-d12a-454d-b986-b77197bb0d83\"}"],
        )],
    )


class SchedulingExecutor(AgentExecutor):
    def __init__(self, settings: Settings, obo: OnBehalfOf, http: httpx.AsyncClient, platform_factory=None):
        self._settings = settings
        self._obo = obo
        self._http = http
        self._platform_factory = platform_factory or (lambda token: HttpPlatform(http, settings.platform_base_url, token))

    async def execute(self, context: RequestContext, event_queue: EventQueue) -> None:
        task = context.current_task or new_task_from_user_message(context.message)
        if not context.current_task:
            await event_queue.enqueue_event(task)
        updater = TaskUpdater(event_queue, task.id, task.context_id)

        def say(text: str):
            return new_text_message(text, context_id=task.context_id, task_id=task.id, role=Role.ROLE_AGENT)

        user = getattr(context.call_context.user, "_user", None)
        if not isinstance(user, Caller):
            await updater.reject(say("Sign-in required."))
            return
        data = next((d for d in get_data_parts(context.message.parts) if isinstance(d, dict)), {})
        project_id = data.get("projectId")
        if not isinstance(project_id, str):
            await updater.reject(say("Send a data part {\"projectId\": \"...\"}."))
            return
        await updater.start_work()
        try:
            token = await self._obo.platform_token(user.token)
            result = await propose(self._platform_factory(token), project_id)
        except DelegationError:
            await updater.failed(say("Couldn't act on your behalf right now."))
            return
        except PlatformError as e:
            await updater.failed(say("That project isn't available." if e.status in (400, 403, 404) else "The platform is unavailable right now."))
            return
        await updater.add_artifact([new_data_part(result), new_text_part(result["message"])], name="visit-proposals")
        await updater.complete()

    async def cancel(self, context: RequestContext, event_queue: EventQueue) -> None:
        raise NotImplementedError("Proposals are quick; nothing to cancel.")
