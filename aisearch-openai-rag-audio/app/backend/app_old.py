import logging
import os
from pathlib import Path

from aiohttp import web
from azure.core.credentials import AzureKeyCredential
from azure.identity import AzureDeveloperCliCredential, DefaultAzureCredential
from dotenv import load_dotenv

from ragtools import attach_rag_tools
from rtmt import RTMiddleTier

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("voicerag")


async def create_app():

    # Load .env during local dev
    if not os.environ.get("RUNNING_IN_PRODUCTION"):
        logger.info("Running in development mode, loading from .env file")
        load_dotenv()

    llm_key = os.environ.get("AZURE_OPENAI_API_KEY")
    search_key = os.environ.get("AZURE_SEARCH_API_KEY")

    # Determine credential mode
    credential = None
    if not llm_key or not search_key:
        if tenant_id := os.environ.get("AZURE_TENANT_ID"):
            logger.info("Using AzureDeveloperCliCredential with tenant_id %s", tenant_id)
            credential = AzureDeveloperCliCredential(
                tenant_id=tenant_id,
                process_timeout=60
            )
        else:
            logger.info("Using DefaultAzureCredential")
            credential = DefaultAzureCredential()

    llm_credential = AzureKeyCredential(llm_key) if llm_key else credential
    search_credential = AzureKeyCredential(search_key) if search_key else credential

    # -----------------------------
    # Create Web App
    # -----------------------------
    app = web.Application()

    # -----------------------------
    # Initialize Realtime Middleware
    # -----------------------------
    rtmt = RTMiddleTier(
        credentials=llm_credential,
        endpoint=os.environ["AZURE_OPENAI_ENDPOINT"],
        deployment=os.environ["AZURE_OPENAI_REALTIME_DEPLOYMENT"],
        voice_choice=os.environ.get("AZURE_OPENAI_REALTIME_VOICE_CHOICE") or "alloy"
    )

    # -----------------------------------------------------------------
    # UPDATED SYSTEM MESSAGE (Only change requested)
    # -----------------------------------------------------------------
    rtmt.system_message = """
            You are a RAG assistant.

            When the user asks anything factual:
            1. ALWAYS call the `search` tool first.
            2. ALWAYS call the `report_grounding` tool using the `doc_id` values returned from search.
            3. After the grounding tool returns results,
            YOU MUST generate a final natural-language answer for the user.

            Your final answer MUST:
            - Use the content from the grounding tool results.
            - Include citations as: [source: DOC_ID]
            - Be returned as a normal assistant message (NOT a tool call).

            Never stop after grounding. Always send a final answer to the user.
            """.strip()

    # -----------------------------
    # Attach RAG Tools
    # -----------------------------
    attach_rag_tools(
        rtmt,
        credentials=search_credential,
        search_endpoint=os.environ.get("AZURE_SEARCH_ENDPOINT"),
        search_index=os.environ.get("AZURE_SEARCH_INDEX"),
        semantic_configuration=None,
        identifier_field="id",            # correct field
        content_field="content",
        embedding_field="contentVector",
        title_field="",                   # no title field in your index
        use_vector_query=(os.getenv("AZURE_SEARCH_USE_VECTOR_QUERY", "true") == "true")
    )

    # -----------------------------
    # Attach RTMT to Websocket Route
    # -----------------------------
    rtmt.attach_to_app(app, "/realtime")

    # -----------------------------
    # Static File Serving for UI
    # -----------------------------
    current_directory = Path(__file__).parent
    app.add_routes([web.get('/', lambda _: web.FileResponse(current_directory / 'static/index.html'))])
    app.router.add_static('/', path=current_directory / 'static', name='static')

    return app


if __name__ == "__main__":
    host = "localhost"
    port = 8765
    web.run_app(create_app(), host=host, port=port)
