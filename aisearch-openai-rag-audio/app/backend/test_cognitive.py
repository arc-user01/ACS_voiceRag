import requests
from azure.search.documents.indexes import SearchIndexClient
from azure.core.credentials import AzureKeyCredential

SEARCH_ENDPOINT = "https://pura-azure-ai-search.search.windows.net"
SEARCH_KEY = "28seqpGAItRApvP2YJTAAyk1lsuByLFJocohPfD9HHAzSeDtq1jG"
INDEX = "pura_lab_reports_development"

###############################################
# 1. Print basic fields using SDK
###############################################

client = SearchIndexClient(
    endpoint=SEARCH_ENDPOINT,
    credential=AzureKeyCredential(SEARCH_KEY)
)

idx = client.get_index(INDEX)

print("\n=== Basic Field List ===\n")
for f in idx.fields:
    print(f"{f.name} -> {f.type}")


###############################################
# 2. Print FULL metadata using REST API
###############################################
API_VERSION = "2023-11-01"   # Always safe version

url = f"{SEARCH_ENDPOINT}/indexes/{INDEX}/docs?api-version={API_VERSION}&search=*"

headers = {
    "api-key": SEARCH_KEY,
    "Content-Type": "application/json"
}

response = requests.get(url, headers=headers)
data = response.json()

# print("\n=== DOCUMENTS IN INDEX ===\n")
# for doc in data["value"]:
#     print(doc)
#     print("------------------------------------")