"""ASGI entry for uvicorn (Aspire: AddUvicornApp(..., "app.asgi:app")). Settings come from the environment."""
import logging

from .main import create_app

logging.basicConfig(level=logging.INFO)
app = create_app()
