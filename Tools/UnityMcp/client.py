"""Call the configured Unity MCP through the SDK when native tools are unavailable.

Run with ~/.codex/unity-mcp/Server/.venv/Scripts/python.exe.
Examples: client.py list; client.py resources; client.py call read_console '{"count":10}'
"""
import argparse
import asyncio
import json
import os
from pathlib import Path
import tomllib

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["list", "resources", "read", "call"])
    parser.add_argument("name", nargs="?")
    parser.add_argument("arguments", nargs="?", default="{}")
    args = parser.parse_args()
    if args.action in {"call", "read"} and not args.name:
        parser.error("call/read requires a tool name or resource URI")
    config = tomllib.loads((Path.home() / ".codex/config.toml").read_text(encoding="utf-8"))
    server = config["mcp_servers"]["unity"]
    parameters = StdioServerParameters(
        command=server["command"], args=server.get("args", []),
        env={**os.environ, **server.get("env", {})}, cwd=server.get("cwd"),
    )
    async with stdio_client(parameters) as (reader, writer):
        async with ClientSession(reader, writer) as session:
            await session.initialize()
            if args.action == "list":
                result = await session.list_tools()
            elif args.action == "resources":
                result = await session.list_resources()
            elif args.action == "read":
                result = await session.read_resource(args.name)
            else:
                result = await session.call_tool(args.name, json.loads(args.arguments))
            print(result.model_dump_json(indent=2))
            if getattr(result, "isError", False):
                raise SystemExit(1)
            structured = getattr(result, "structuredContent", None)
            if isinstance(structured, dict):
                payload = structured.get("result", structured)
                if isinstance(payload, dict) and payload.get("success") is False:
                    raise SystemExit(1)


if __name__ == "__main__":
    asyncio.run(main())
