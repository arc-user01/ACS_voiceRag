import logging
import os
from pathlib import Path
import json

from aiohttp import web
from azure.core.credentials import AzureKeyCredential
from azure.identity import AzureDeveloperCliCredential, DefaultAzureCredential
from dotenv import load_dotenv

from ragtools import attach_rag_tools
from rtmt import RTMiddleTier

# Import for direct search access
from azure.search.documents.aio import SearchClient
from azure.search.documents.models import VectorizableTextQuery
from azure.search.documents.indexes.aio import SearchIndexClient

# Import for GPT-based answer generation
from openai import AsyncAzureOpenAI

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("voicerag")

# Global variables for REST endpoint
rtmt_instance = None
search_client_global = None
search_config = {}
openai_client_global = None


async def query_handler(request):
    """
    REST API endpoint for text queries from Azure Communication Services.
    Uses Azure AI Search + GPT to generate tailored answers.
    """
    try:
        data = await request.json()
        question = data.get('question', '')
        
        if not question:
            return web.json_response({
                "error": "No question provided"
            }, status=400)
        
        logger.info(f"📩 REST Query received: {question}")
        
        if not search_client_global:
            return web.json_response({
                "error": "Search client not initialized"
            }, status=500)
        
        # 1. Perform search to get relevant documents
        search_results = await perform_search(question)
        
        if search_results and len(search_results) > 0:
            # 2. Use GPT to generate query-specific answer from raw content
            answer = await generate_answer_with_gpt(question, search_results)
            
            return web.json_response({
                "answer": answer,
                "status": "success",
                "question": question,
                "sources": search_results
            })
        else:
            return web.json_response({
                "answer": "I couldn't find relevant information in the knowledge base to answer your question.",
                "status": "success",
                "question": question,
                "sources": []
            })
        
    except Exception as e:
        logger.error(f"❌ Query handler error: {str(e)}")
        import traceback
        traceback.print_exc()
        return web.json_response({
            "error": str(e),
            "status": "error"
        }, status=500)


async def perform_search(query):
    """
    Perform Azure AI Search - same logic as ragtools._search_tool
    """
    try:
        logger.info(f"🔍 Searching for: {query}")
        
        # Build vector query if enabled
        vector_queries = []
        if search_config.get('use_vector_query'):
            vector_queries.append(
                VectorizableTextQuery(
                    text=query,
                    k_nearest_neighbors=5,
                    fields=search_config.get('embedding_field'),
                )
            )
        
        # Execute search
        search_results = await search_client_global.search(
            search_text=query,
            query_type="semantic" if search_config.get('semantic_configuration') else "simple",
            semantic_configuration_name=search_config.get('semantic_configuration'),
            select=[
                search_config.get('identifier_field'),
                search_config.get('content_field')
            ],
            vector_queries=vector_queries,
            top=5,
        )
        
        # Collect results
        results = []
        async for doc in search_results:
            results.append({
                "doc_id": doc.get(search_config.get('identifier_field')),
                "content": doc.get(search_config.get('content_field')),
            })
        
        logger.info(f"✅ Found {len(results)} documents")
        return results
        
    except Exception as e:
        logger.error(f"❌ Search error: {str(e)}")
        import traceback
        traceback.print_exc()
        return []


async def generate_answer_with_gpt(question, search_results):
    """
    Use GPT to generate a query-specific answer from raw search content.
    The response length adapts to the complexity of the question.
    """
    if not search_results or not openai_client_global:
        return "I couldn't find relevant information to answer your question."
    
    try:
        # Combine top search results as context
        context_parts = []
        sources = []
        
        for result in search_results[:3]:  # Top 3 results
            doc_id = result.get('doc_id', 'unknown')
            content = result.get('content', '')
            if content:
                context_parts.append(f"[Document {doc_id}]\n{content[:1000]}")
                sources.append(doc_id)
        
        context = "\n\n".join(context_parts)
        
        logger.info(f"🤖 Generating GPT answer with {len(search_results)} sources")
        
        # Call GPT to summarize and answer
        response = await openai_client_global.chat.completions.create(
            model=os.getenv("AZURE_OPENAI_CHAT_DEPLOYMENT", "gpt-4"),
            messages=[
                {
                    "role": "system",
                    "content": "You are a knowledgeable assistant. Answer questions directly and concisely using ONLY the provided context. Adapt your response length to the question's complexity - simple questions get brief answers, complex questions get detailed explanations. Always cite sources at the end."
                },
                {
                    "role": "user",
                    "content": f"Context:\n{context}\n\nQuestion: {question}\n\nProvide a direct, appropriate answer based solely on the context above."
                }
            ],
            temperature=0.3,
            max_tokens=500
        )
        
        answer = response.choices[0].message.content.strip()
        
        # Add sources if not already included
        if sources and "[source" not in answer.lower():
            answer += f" [sources: {', '.join(sources)}]"
        
        logger.info(f"✅ Generated answer ({len(answer)} chars)")
        return answer
        
    except Exception as e:
        logger.error(f"❌ GPT generation error: {str(e)}")
        # Fallback to simple format
        return format_answer_simple(search_results)


def format_answer_simple(search_results):
    """
    Fallback: Simple answer formatting without GPT
    """
    if not search_results:
        return "I couldn't find relevant information to answer your question."
    
    top_result = search_results[0]
    content = top_result.get('content', '')[:300]
    doc_id = top_result.get('doc_id', 'unknown')
    
    return f"{content}... [source: {doc_id}]"


async def health_handler(request):
    """Health check endpoint"""
    return web.json_response({
        "status": "healthy",
        "service": "VoiceRAG",
        "version": "1.0.0",
        "endpoints": {
            "/query": "POST - Text queries with RAG",
            "/realtime": "WebSocket - Voice queries with RAG",
            "/health": "GET - Health check"
        },
        "rtmt_initialized": rtmt_instance is not None,
        "search_initialized": search_client_global is not None,
        "model": os.environ.get("AZURE_OPENAI_REALTIME_DEPLOYMENT")
    })


async def discover_indexes(search_endpoint, credential):
    """
    Auto-discover available Azure AI Search indexes
    """
    try:
        client = SearchIndexClient(endpoint=search_endpoint, credential=credential)
        indexes = []
        async for index in client.list_indexes():
            indexes.append({
                "name": index.name,
                "fields_count": len(index.fields)
            })
        await client.close()
        
        logger.info(f"📚 Found {len(indexes)} available indexes:")
        for idx in indexes:
            logger.info(f"   - {idx['name']} ({idx['fields_count']} fields)")
        
        return indexes
    except Exception as e:
        logger.error(f"❌ Index discovery error: {str(e)}")
        return []


async def create_app():
    global rtmt_instance, search_client_global, search_config, openai_client_global

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
    # Initialize Azure OpenAI Client for GPT Chat Completions
    # (Separate from Realtime API for voice)
    # -----------------------------
    try:
        chat_endpoint = os.getenv("AZURE_OPENAI_CHAT_ENDPOINT")
        chat_key = os.getenv("AZURE_OPENAI_CHAT_API_KEY")
        chat_api_version = os.getenv("AZURE_OPENAI_CHAT_API_VERSION", "2024-08-01-preview")
        
        if chat_endpoint and chat_key:
            openai_client_global = AsyncAzureOpenAI(
                api_key=chat_key,
                api_version=chat_api_version,
                azure_endpoint=chat_endpoint
            )
            logger.info(f"✅ Azure OpenAI Chat client initialized (GPT-4.1 for text summaries)")
        else:
            logger.warning("⚠️ Chat endpoint not configured, will use fallback formatting")
    except Exception as e:
        logger.error(f"❌ Failed to initialize OpenAI chat client: {str(e)}")

    # -----------------------------
    # Initialize Search Client with Auto-Discovery
    # -----------------------------
    search_endpoint = os.environ.get("AZURE_SEARCH_ENDPOINT")
    search_index = os.environ.get("AZURE_SEARCH_INDEX")
    
    if search_endpoint:
        try:
            # Ensure token for managed identity
            if not isinstance(search_credential, AzureKeyCredential):
                search_credential.get_token("https://search.azure.com/.default")
                logger.info("Using DefaultAzureCredential for search")
            
            # Auto-discover indexes if not explicitly set
            if not search_index:
                logger.info("🔍 AZURE_SEARCH_INDEX not set, discovering available indexes...")
                available_indexes = await discover_indexes(search_endpoint, search_credential)
                
                if available_indexes:
                    search_index = available_indexes[0]['name']
                    logger.info(f"✅ Auto-selected index: {search_index}")
                else:
                    logger.error("❌ No indexes found!")
                    search_index = None
            
            if search_index:
                search_client_global = SearchClient(
                    endpoint=search_endpoint,
                    index_name=search_index,
                    credential=search_credential,
                    user_agent="VoiceRAG-REST"
                )
                
                # Store search configuration
                search_config = {
                    'index_name': search_index,
                    'semantic_configuration': None,
                    'identifier_field': 'id',
                    'content_field': 'content',
                    'embedding_field': 'contentVector',
                    'use_vector_query': (os.getenv("AZURE_SEARCH_USE_VECTOR_QUERY", "true") == "true")
                }
                
                logger.info(f"✅ Search client initialized: {search_index}")
            
        except Exception as e:
            logger.error(f"❌ Failed to initialize search client: {str(e)}")

    # -----------------------------
    # Create Web App
    # -----------------------------
    app = web.Application()

    # -----------------------------
    # Initialize Realtime Middleware (for voice)
    # -----------------------------
    rtmt = RTMiddleTier(
        credentials=llm_credential,
        endpoint=os.environ["AZURE_OPENAI_ENDPOINT"],
        deployment=os.environ["AZURE_OPENAI_REALTIME_DEPLOYMENT"],
        voice_choice=os.environ.get("AZURE_OPENAI_REALTIME_VOICE_CHOICE") or "alloy"
    )

    rtmt_instance = rtmt

    # -----------------------------
    # System Message (for voice)
    # -----------------------------
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
    # Attach RAG Tools (for voice)
    # -----------------------------
    attach_rag_tools(
        rtmt,
        credentials=search_credential,
        search_endpoint=search_endpoint,
        search_index=search_index,
        semantic_configuration=None,
        identifier_field="id",
        content_field="content",
        embedding_field="contentVector",
        title_field="",
        use_vector_query=(os.getenv("AZURE_SEARCH_USE_VECTOR_QUERY", "true") == "true")
    )

    # -----------------------------
    # REST API Routes (NEW!)
    # -----------------------------
    app.router.add_post('/query', query_handler)
    app.router.add_get('/health', health_handler)

    # -----------------------------
    # Attach RTMT to WebSocket Route (for voice)
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
    logger.info("=" * 70)
    logger.info("🚀 VoiceRAG Server Starting")
    logger.info("=" * 70)
    logger.info(f"🎤 Voice (WebSocket): ws://{host}:{port}/realtime")
    logger.info(f"💬 Text (REST API):   http://{host}:{port}/query")
    logger.info(f"💚 Health Check:      http://{host}:{port}/health")
    logger.info(f"🌐 Web UI:            http://{host}:{port}/")
    logger.info(f"🤖 Model:             {os.environ.get('AZURE_OPENAI_REALTIME_DEPLOYMENT')}")
    logger.info("=" * 70)
    web.run_app(create_app(), host=host, port=port)
