#!/bin/sh
set -e

echo ""
echo "====================================="
echo " Running start.sh inside Docker"
echo "====================================="
echo ""

##############################################
# 1. Build Frontend
##############################################

echo "Building frontend..."
#cd /aisearch-openai-rag-audio/app/frontend

#npm install
#npm run build || true

echo "Frontend build completed."
echo ""

##############################################
# 2. Install Backend Dependencies
##############################################

echo "Installing backend Python dependencies..."
cd /aisearch-openai-rag-audio/app/backend

pip install --no-cache-dir -r requirements.txt

echo "Backend dependencies installed."
echo ""

##############################################
# 3. AUTO-INSTALL missing Python packages
##############################################

echo "Checking backend imports..."

python3 - << 'EOF'
import importlib, sys, subprocess

required_modules = [
    "aiohttp",
    "azure.identity",
    "azure.search.documents",
    "dotenv",
    "azure.storage.blob",
    "gunicorn",
    "rich"
]

missing = []

for module in required_modules:
    name = module.split('.')[0]
    try:
        importlib.import_module(name)
    except ImportError:
        missing.append(name)

if missing:
    print(f"Missing modules detected → {missing}")
    subprocess.check_call([sys.executable, "-m", "pip", "install"] + missing)
else:
    print("All required modules already installed.")
EOF

echo ""
echo "Backend auto-install process completed."
echo ""

##############################################
# 4. Start Backend
##############################################

echo "-------------------------------------"
echo " Backend server is now starting..."
echo " Access URLs:"
echo "   Inside Docker: http://0.0.0.0:8765"
echo "   On your machine: http://localhost:8765"
echo "-------------------------------------"
echo ""

python3 app.py

echo "Backend server exited with code $?"
