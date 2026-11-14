"""The scheduling agent's web app.

    GET  /.well-known/agent-card.json   public: what this agent does and how to call it (A2A discovery)
    POST /a2a                           A2A JSON-RPC (protocol 1.0); a bearer token FOR this agent required
    GET  /health                        liveness for Aspire

Traces and logs go to the Aspire dashboard over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set, and the
W3C trace context is continued from the caller and passed on to the platform, so one customer question
is one trace across the .NET agent, this agent and the platform services.
"""
import logging
import os
from contextlib import asynccontextmanager

import httpx
from a2a.server.request_handlers import DefaultRequestHandler
from a2a.server.routes.agent_card_routes import create_agent_card_routes
from a2a.server.routes.jsonrpc_routes import create_jsonrpc_routes
from a2a.server.tasks import InMemoryTaskStore
from starlette.applications import Starlette
from starlette.authentication import AuthenticationError
from starlette.middleware import Middleware
from starlette.middleware.authentication import AuthenticationMiddleware
from starlette.requests import HTTPConnection
from starlette.responses import JSONResponse, PlainTextResponse
from starlette.routing import Route

from .agent import SchedulingExecutor, agent_card
from .auth import BearerAuth, OnBehalfOf
from .config import Settings


def _telemetry(app: Starlette) -> None:
    if not os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT"):
        return
    from opentelemetry import trace
    from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
    from opentelemetry.instrumentation.httpx import HTTPXClientInstrumentor
    from opentelemetry.instrumentation.starlette import StarletteInstrumentor
    from opentelemetry.sdk.resources import Resource
    from opentelemetry.sdk.trace import TracerProvider
    from opentelemetry.sdk.trace.export import BatchSpanProcessor

    provider = TracerProvider(resource=Resource.create({"service.name": os.environ.get("OTEL_SERVICE_NAME", "scheduling-agent")}))
    provider.add_span_processor(BatchSpanProcessor(OTLPSpanExporter()))
    trace.set_tracer_provider(provider)
    StarletteInstrumentor.instrument_app(app)
    HTTPXClientInstrumentor().instrument()


class RequireCaller:
    """A2A calls need an authenticated caller; the card and health stay public."""

    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope["type"] == "http" and scope["path"].startswith("/a2a"):
            user = scope.get("user")
            if user is None or not getattr(user, "is_authenticated", False):
                response = JSONResponse({"error": "unauthorized"}, status_code=401, headers={"WWW-Authenticate": "Bearer"})
                await response(scope, receive, send)
                return
        await self.app(scope, receive, send)


def _auth_error(conn: HTTPConnection, exc: AuthenticationError):
    return JSONResponse({"error": "unauthorized"}, status_code=401, headers={"WWW-Authenticate": 'Bearer error="invalid_token"'})


def create_app(settings: Settings | None = None, http: httpx.AsyncClient | None = None, auth: BearerAuth | None = None,
               platform_factory=None) -> Starlette:
    settings = settings or Settings.from_env()
    http = http or httpx.AsyncClient()
    card = agent_card(settings)
    executor = SchedulingExecutor(settings, OnBehalfOf(settings, http), http, platform_factory)
    handler = DefaultRequestHandler(agent_executor=executor, task_store=InMemoryTaskStore(), agent_card=card)

    async def health(_):
        return PlainTextResponse("Healthy")

    @asynccontextmanager
    async def lifespan(_):
        yield
        await http.aclose()

    app = Starlette(
        routes=[*create_agent_card_routes(card), *create_jsonrpc_routes(handler, "/a2a"), Route("/health", health)],
        middleware=[Middleware(AuthenticationMiddleware, backend=auth or BearerAuth(settings), on_error=_auth_error), Middleware(RequireCaller)],
        lifespan=lifespan,
    )
    _telemetry(app)
    return app


def app_factory() -> Starlette:   # uvicorn app.main:app_factory --factory
    logging.basicConfig(level=logging.INFO)
    return create_app()
