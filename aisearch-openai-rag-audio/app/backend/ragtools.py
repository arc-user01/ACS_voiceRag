import re
import logging
from typing import Any

from azure.core.credentials import AzureKeyCredential
from azure.identity import DefaultAzureCredential
from azure.search.documents.aio import SearchClient
from azure.search.documents.models import VectorizableTextQuery

from rtmt import RTMiddleTier, Tool, ToolResult, ToolResultDirection

logger = logging.getLogger("voicerag.ragtools")


# ============================================================
# SEARCH TOOL SCHEMA
# ============================================================
_search_tool_schema = {
    "type": "function",
    "name": "search",
    "description": "Search the knowledge base using keyword and vector search.",
    "parameters": {
        "type": "object",
        "properties": {
            "query": {
                "type": "string",
                "description": "User search query string."
            }
        },
        "required": ["query"],
        "additionalProperties": False,
    },
}


# ============================================================
# GROUNDING TOOL SCHEMA
# ============================================================
_grounding_tool_schema = {
    "type": "function",
    "name": "report_grounding",
    "description": "Fetch the cited documents and return their content.",
    "parameters": {
        "type": "object",
        "properties": {
            "sources": {
                "type": "array",
                "items": {"type": "string"},
                "description": "List of document IDs used in the answer."
            }
        },
        "required": ["sources"],
        "additionalProperties": False,
    },
}


# ============================================================
# SEARCH TOOL EXECUTION
# ============================================================
async def _search_tool(
    search_client: SearchClient,
    semantic_configuration: str | None,
    identifier_field: str,
    content_field: str,
    embedding_field: str,
    use_vector_query: bool,
    args: Any,
) -> ToolResult:

    logger.info(f"[SEARCH] Incoming args → {args}")

    query = args.get("query") if isinstance(args, dict) else None
    if not query:
        logger.error("[SEARCH] Missing query!")
        return ToolResult({"error": "Missing 'query'"}, ToolResultDirection.TO_SERVER)

    logger.info(f"[SEARCH] Running search for: {query}")

    # Build vector query if vector search is enabled
    vector_queries = []
    if use_vector_query:
        vector_queries.append(
            VectorizableTextQuery(
                text=query,
                k_nearest_neighbors=5,
                fields=embedding_field,
            )
        )

    # Execute Azure Cognitive Search
    try:
        search_results = await search_client.search(
            search_text=query,
            query_type="semantic" if semantic_configuration else "simple",
            semantic_configuration_name=semantic_configuration,
            select=[identifier_field, content_field],
            vector_queries=vector_queries,
            top=5,
        )
    except Exception as e:
        logger.error(f"[SEARCH] ERROR → {e}")
        return ToolResult({"error": str(e)}, ToolResultDirection.TO_SERVER)

    output = []
    async for doc in search_results:
        output.append({
            "doc_id": doc.get(identifier_field),
            "content": doc.get(content_field),
        })

    logger.info(f"[SEARCH] Output (#={len(output)}) → {output}")

    return ToolResult({"results": output}, ToolResultDirection.TO_SERVER)


# ============================================================
# GROUNDING TOOL EXECUTION
# ============================================================
KEY_PATTERN = re.compile(r"^[a-zA-Z0-9_\-]+$")  # allow alphanumerics, underscore, hyphen


async def _report_grounding_tool(
    search_client: SearchClient,
    identifier_field: str,
    title_field: str,     # unused but kept for compatibility
    content_field: str,
    args: Any,
):

    logger.info(f"[GROUNDING] Incoming args → {args}")

    sources = args.get("sources") if isinstance(args, dict) else None

    if not sources:
        logger.info("[GROUNDING] No sources → return empty list.")
        return ToolResult({"sources": []}, ToolResultDirection.TO_CLIENT)

    # Validate IDs
    valid_sources = [s for s in sources if KEY_PATTERN.match(s)]
    logger.info(f"[GROUNDING] Valid IDs → {valid_sources}")

    if not valid_sources:
        logger.warning("[GROUNDING] All doc IDs invalid.")
        return ToolResult({"sources": []}, ToolResultDirection.TO_CLIENT)

    # Pattern: "id1 OR id2 OR id3"
    search_text = " OR ".join(valid_sources)
    logger.info(f"[GROUNDING] Fetch query → {search_text}")

    try:
        search_results = await search_client.search(
            search_text=search_text,
            search_fields=["content"],
            select=[identifier_field, content_field],
            query_type="full",
            top=len(valid_sources),
        )
    except Exception as e:
        logger.error(f"[GROUNDING] ERROR → {e}")
        return ToolResult({"sources": []}, ToolResultDirection.TO_CLIENT)

    docs = []
    async for r in search_results:
        docs.append({
            "doc_id": r.get(identifier_field),
            "content": r.get(content_field),
        })
        logger.debug(f"[GROUNDING] Retrieved doc → {docs[-1]}")

    logger.info("[GROUNDING] Completed successfully.")

    return ToolResult({"sources": docs}, ToolResultDirection.TO_CLIENT)


# ============================================================
# ATTACH RAG TOOLS TO RTMT
# ============================================================
def attach_rag_tools(
    rtmt: RTMiddleTier,
    credentials: AzureKeyCredential | DefaultAzureCredential,
    search_endpoint: str,
    search_index: str,
    semantic_configuration: str | None,
    identifier_field: str,
    content_field: str,
    embedding_field: str,
    title_field: str,
    use_vector_query: bool,
):

    logger.info("[RAG] Initializing Azure Cognitive Search client...")

    # Ensure token for managed identity
    if not isinstance(credentials, AzureKeyCredential):
        credentials.get_token("https://search.azure.com/.default")
        logger.info("[RAG] Using DefaultAzureCredential / Managed Identity")

    search_client = SearchClient(
        endpoint=search_endpoint,
        index_name=search_index,
        credential=credentials,
        user_agent="RTMiddleTier",
    )

    # Register SEARCH tool
    logger.info("[RAG] Registering SEARCH tool")
    rtmt.tools["search"] = Tool(
        schema=_search_tool_schema,
        target=lambda args: _search_tool(
            search_client,
            semantic_configuration,
            identifier_field,
            content_field,
            embedding_field,
            use_vector_query,
            args,
        ),
    )

    # Register GROUNDING tool
    logger.info("[RAG] Registering GROUNDING tool")
    rtmt.tools["report_grounding"] = Tool(
        schema=_grounding_tool_schema,
        target=lambda args: _report_grounding_tool(
            search_client,
            identifier_field,
            title_field,
            content_field,
            args,
        ),
    )

    logger.info("[RAG] Tools attached successfully.")
