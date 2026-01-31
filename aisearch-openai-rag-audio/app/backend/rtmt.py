import asyncio
import json
import logging
from enum import Enum
from typing import Any

import aiohttp
from aiohttp import web
from azure.core.credentials import AzureKeyCredential
from azure.identity import DefaultAzureCredential, get_bearer_token_provider

logger = logging.getLogger("voicerag.rtmt")

WS_IDLE_TIMEOUT = 300


###########################################################
# SAFE PARSER
###########################################################
def safe_json_loads(s: str):
    try:
        return json.loads(s)
    except Exception:
        logger.error("❌ Failed to parse JSON: %s", s)
        return {}


###########################################################
# TOOL RESULT / TOOL
###########################################################
class ToolResultDirection(Enum):
    TO_SERVER = 1
    TO_CLIENT = 2


class ToolResult:
    def __init__(self, text, dest):
        self.text = text
        self.destination = dest

    def to_text(self):
        if self.text is None:
            return ""
        return self.text if isinstance(self.text, str) else json.dumps(self.text)


class Tool:
    def __init__(self, target, schema):
        self.target = target
        self.schema = schema


###########################################################
# REALTIME MIDDLE TIER
###########################################################
class RTMiddleTier:

    def __init__(self, endpoint, deployment, credentials, voice_choice=None):
        self.endpoint = endpoint
        self.deployment = deployment
        self.voice_choice = voice_choice

        self.system_message = None
        self.tools = {}
        self.last_user_message = None
        self.api_version = "2024-10-01-preview"

        if isinstance(credentials, AzureKeyCredential):
            self.key = credentials.key
            self._token_provider = None
        else:
            self.key = None
            self._token_provider = get_bearer_token_provider(
                credentials,
                "https://cognitiveservices.azure.com/.default",
            )
            self._token_provider()

    ###########################################################
    # CLIENT → AI
    ###########################################################
    async def _process_message_to_server(self, msg, ws):
        logger.info("➡️ CLIENT → RTMT: %s", msg.data)

        try:
            message = json.loads(msg.data)
        except:
            return msg.data

        updated = msg.data
        msg_type = message.get("type", "")

        if msg_type == "conversation.item.create":
            item = message.get("item", {})
            if item.get("type") == "message":
                content = item.get("content")
                if isinstance(content, list) and content:
                    self.last_user_message = content[0].get("text")
                elif isinstance(content, dict):
                    self.last_user_message = content.get("text")
                logger.info("💬 User said: %s", self.last_user_message)

        elif msg_type == "session.update":
            session = message["session"]

            if self.system_message:
                session["instructions"] = self.system_message

            session["tools"] = [t.schema for t in self.tools.values()]
            session["tool_choice"] = "auto"

            if self.voice_choice:
                session["voice"] = self.voice_choice

            updated = json.dumps(message)

        return updated

    ###########################################################
    # AI → CLIENT
    ###########################################################
    async def _process_message_to_client(self, msg, client_ws, server_ws):
        logger.info("⬅️ AI → RTMT: %s", msg.data)

        # ----------------------------
        # Parse JSON safely
        # ----------------------------
        try:
            message = json.loads(msg.data)
        except Exception:
            return msg.data

        msg_type = message.get("type", "")

        # ------------------------------------------------------
        # SAFE SEND (No warnings, no crash)
        # ------------------------------------------------------
        async def safe_send(ws, data):
            if ws.closed:
                # IMPORTANT: no warning, no error → avoid spam
                logger.info("ℹ️ Browser WS closed — skipping send.")
                return True  # Pretend success so flow continues
            try:
                await ws.send_str(data)
                return True
            except Exception as e:
                logger.info(f"ℹ️ Browser send skipped: {e}")
                return True

        # ------------------------------------------------------
        # TOOL CALL EXECUTION
        # ------------------------------------------------------
        if msg_type == "response.output_item.done":
            item = message.get("item", {})

            if item.get("type") == "function_call":
                tool_name = item["name"]
                args = safe_json_loads(item["arguments"])

                logger.info(f"🔧 TOOL CALL → {tool_name} | args={args}")

                if tool_name == "search":
                    args["query"] = self.last_user_message or ""

                tool = self.tools[tool_name]
                result = await tool.target(args)

                await server_ws.send_json({
                    "type": "conversation.item.create",
                    "item": {
                        "type": "function_call_output",
                        "call_id": item["call_id"],
                        "output": result.to_text(),
                    }
                })
                return None

        # ------------------------------------------------------
        # FINAL ANSWER HANDLING — CLIENT ONLY (Option 1)
        # ------------------------------------------------------
        if msg_type == "conversation.item.created":
            logger.info("🟢 FINAL MESSAGE → CLIENT ONLY")
            await safe_send(client_ws, msg.data)
            return None

        if msg_type == "response.done":
            logger.info("🟢 FINAL response.done → CLIENT ONLY")
            await safe_send(client_ws, msg.data)
            return None

        # ------------------------------------------------------
        # DEFAULT PASS-THROUGH
        # ------------------------------------------------------
        return msg.data

    ###########################################################
    # WS FORWARDING
    ###########################################################
    async def _forward_messages(self, ws):

        async with aiohttp.ClientSession(base_url=self.endpoint) as session:
            params = {"deployment": self.deployment, "api-version": self.api_version}
            headers = (
                {"api-key": self.key}
                if self.key
                else {"Authorization": f"Bearer {self._token_provider()}"}
            )

            async with session.ws_connect(
                "/openai/realtime",
                params=params,
                headers=headers
            ) as target_ws:

                async def from_client():
                    async for msg in ws:
                        if msg.type == aiohttp.WSMsgType.TEXT:
                            updated = await self._process_message_to_server(msg, ws)
                            if updated:
                                await target_ws.send_str(updated)

                async def from_server():
                    async for msg in target_ws:
                        if msg.type == aiohttp.WSMsgType.TEXT:
                            updated = await self._process_message_to_client(
                                msg, ws, target_ws
                            )
                            if updated:
                                await ws.send_str(updated)

                await asyncio.gather(from_client(), from_server())

    ###########################################################
    # ENTRYPOINT
    ###########################################################
    async def _websocket_handler(self, request):
        ws = web.WebSocketResponse(heartbeat=30, timeout=WS_IDLE_TIMEOUT)
        await ws.prepare(request)

        try:
            await self._forward_messages(ws)
        except Exception as e:
            logger.error("❌ WS Error: %s", e)

        return ws

    def attach_to_app(self, app, path):
        app.router.add_get(path, self._websocket_handler)
