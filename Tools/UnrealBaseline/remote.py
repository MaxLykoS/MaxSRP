"""Use Epic's loopback-only Python transport for this specific UE project."""
import argparse
import json
from pathlib import Path
import sys
import time

sys.path.insert(0, r"D:\Program Files (x86)\EPIC\UE_5.3\Engine\Plugins\Experimental\PythonScriptPlugin\Content\Python")
import remote_execution

parser = argparse.ArgumentParser()
parser.add_argument("script", nargs="?")
parser.add_argument("--code")
args = parser.parse_args()
session = remote_execution.RemoteExecution()
session.start()
try:
    end = time.monotonic() + 8
    nodes = []
    while time.monotonic() < end:
        nodes = [n for n in session.remote_nodes if n.get("project_name") == "我的项目"]
        if nodes:
            break
        time.sleep(.25)
    if len(nodes) != 1:
        raise RuntimeError("Expected exactly one reference UE project: " + json.dumps(session.remote_nodes, ensure_ascii=False))
    session.open_command_connection(nodes[0]["node_id"])
    code = args.code if args.code is not None else Path(args.script).read_text(encoding="utf-8")
    result = session.run_command(code, exec_mode=remote_execution.MODE_EXEC_FILE, raise_on_failure=False)
    print(json.dumps({k:v for k,v in result.items() if k != "command"}, ensure_ascii=False, indent=2))
    if not result["success"]:
        raise SystemExit(1)
finally:
    session.stop()
