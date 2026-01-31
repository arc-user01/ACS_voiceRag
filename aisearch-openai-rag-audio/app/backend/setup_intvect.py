import os
import logging
from dotenv import load_dotenv
from rich.logging import RichHandler

from azure.core.credentials import AzureKeyCredential
from azure.search.documents.indexes import SearchIndexClient
from azure.search.documents.indexes.models import (
    SearchIndex,
    SearchField,
    SearchableField,
    SemanticSearch,
    SemanticConfiguration,
    SemanticField,
    SemanticPrioritizedFields,
)


###############################################
# LOAD LOCAL .env (NO AZD)
###############################################
def load_local_env():
    env_path = os.path.join(os.path.dirname(__file__), ".env")
    if not os.path.exists(env_path):
        raise Exception(f".env file not found at {env_path}")
    load_dotenv(env_path, override=True)
    print(f"Loaded .env from: {env_path}")


###############################################
# CREATE SIMPLE TEXT-ONLY INDEX
###############################################
def setup_index(search_credential):
    index_name = os.environ["AZURE_SEARCH_INDEX"]
    endpoint = os.environ["AZURE_SEARCH_ENDPOINT"]

    index_client = SearchIndexClient(endpoint, search_credential)

    # Check if index already exists
    existing_indexes = [idx.name for idx in index_client.list_indexes()]

    if index_name in existing_indexes:
        logger.info(f"Index '{index_name}' already exists — skipping creation.")
        return

    logger.info(f"Creating text-only Azure AI Search index: {index_name}")

    index = SearchIndex(
        name=index_name,
        fields=[
            SearchableField(name="chunk_id", key=True),
            SearchableField(name="title"),
            SearchableField(name="chunk"),
        ],
        semantic_search=SemanticSearch(
            configurations=[
                SemanticConfiguration(
                    name="default",
                    prioritized_fields=SemanticPrioritizedFields(
                        title_field=SemanticField(field_name="title"),
                        content_fields=[SemanticField(field_name="chunk")]
                    )
                )
            ],
            default_configuration_name="default"
        )
    )

    index_client.create_index(index)
    logger.info(f"Index '{index_name}' created successfully.")


###############################################
# MAIN
###############################################
if __name__ == "__main__":
    logging.basicConfig(
        level=logging.INFO,
        format="%(message)s",
        handlers=[RichHandler(rich_tracebacks=True)],
    )
    logger = logging.getLogger("voicerag")

    # Load local .env file
    load_local_env()

    # Only API key (no AAD, no azd)
    search_credential = AzureKeyCredential(os.environ["AZURE_SEARCH_API_KEY"])

    logger.info("Setting up Azure AI Search index (NO embeddings, NO blob, NO azd)...")
    setup_index(search_credential)

    logger.info("Setup completed (text-only index).")
