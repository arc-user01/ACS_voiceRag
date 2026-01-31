# ------------------------------
# Base image with Python + Node
# ------------------------------
FROM python:3.12-slim AS base

RUN apt-get update && apt-get install -y curl \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y nodejs build-essential bash \
    && apt-get clean


# ------------------------------
# BUILD STAGE  (ONLY frontend build)
# ------------------------------
FROM base AS build-stage

WORKDIR /aisearch-openai-rag-audio

COPY app /aisearch-openai-rag-audio/app
COPY scripts /aisearch-openai-rag-audio/scripts

# Build ONLY the frontend during build stage
RUN cd /aisearch-openai-rag-audio/app/frontend && npm install && npm run build


# ------------------------------
# PRODUCTION STAGE  (runtime only)
# ------------------------------
FROM base AS production-stage

WORKDIR /aisearch-openai-rag-audio

# Copy project with built frontend
COPY --from=build-stage /aisearch-openai-rag-audio /aisearch-openai-rag-audio

# Make start.sh executable
RUN chmod +x /aisearch-openai-rag-audio/scripts/start.sh

EXPOSE 8765

# RUN BACKEND ONLY AT RUNTIME
CMD ["bash", "/aisearch-openai-rag-audio/scripts/start.sh"]
