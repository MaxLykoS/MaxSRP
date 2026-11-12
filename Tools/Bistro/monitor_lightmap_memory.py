"""Watch one explicitly identified MaxSRP bake; never kill or restart Unity.

Run with the MCP Python interpreter and pass its path again with --python.
--sample-only prints one memory sample without connecting to Unity or writing files.
All monitoring output goes to OfflineBaseline/BistroLightmap.
"""

from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time


GIB = 1024 ** 3
EXPECTED_PROJECT = Path("D:/UnityProject/MaxSRP")
LOW_COMMIT_BYTES = 3 * GIB
TERMINAL_PHASES = {"completed", "cancelled", "failed"}


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


class PerformanceInfo(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD)] + [
        (name, ctypes.c_size_t) for name in (
            "CommitTotal", "CommitLimit", "CommitPeak", "PhysicalTotal",
            "PhysicalAvailable", "SystemCache", "KernelTotal", "KernelPaged",
            "KernelNonpaged", "PageSize",
        )
    ] + [(name, wintypes.DWORD) for name in ("HandleCount", "ProcessCount", "ThreadCount")]


class ProcessMemoryCounters(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
        (name, ctypes.c_size_t) for name in (
            "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
            "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage",
            "PagefileUsage", "PeakPagefileUsage", "PrivateUsage",
        )
    ]


class WindowsMemory:
    def __init__(self, pid: int):
        if os.name != "nt":
            raise RuntimeError("This watcher requires Windows.")
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.psapi = ctypes.WinDLL("psapi", use_last_error=True)
        self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel.CloseHandle.restype = wintypes.BOOL
        self.kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.kernel.WaitForSingleObject.restype = wintypes.DWORD
        self.kernel.QueryFullProcessImageNameW.argtypes = [
            wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)
        ]
        self.kernel.QueryFullProcessImageNameW.restype = wintypes.BOOL
        self.psapi.GetPerformanceInfo.argtypes = [
            ctypes.POINTER(PerformanceInfo), wintypes.DWORD
        ]
        self.psapi.GetPerformanceInfo.restype = wintypes.BOOL
        self.psapi.GetProcessMemoryInfo.argtypes = [
            wintypes.HANDLE, ctypes.POINTER(ProcessMemoryCounters), wintypes.DWORD
        ]
        self.psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
        # Keep this handle open: a later process reusing the PID cannot become our target.
        self.handle = self.kernel.OpenProcess(0x1000 | 0x100000, False, pid)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None

    def alive(self) -> bool:
        result = self.kernel.WaitForSingleObject(self.handle, 0)
        if result == 0:
            return False
        if result == 258:  # WAIT_TIMEOUT: process is still running.
            return True
        raise ctypes.WinError(ctypes.get_last_error())

    def image_name(self) -> str:
        buffer = ctypes.create_unicode_buffer(32768)
        size = wintypes.DWORD(len(buffer))
        if not self.kernel.QueryFullProcessImageNameW(self.handle, 0, buffer, ctypes.byref(size)):
            raise ctypes.WinError(ctypes.get_last_error())
        return buffer.value

    def sample(self) -> dict:
        system = PerformanceInfo()
        system.cb = ctypes.sizeof(system)
        process = ProcessMemoryCounters()
        process.cb = ctypes.sizeof(process)
        if not self.psapi.GetPerformanceInfo(ctypes.byref(system), system.cb):
            raise ctypes.WinError(ctypes.get_last_error())
        if not self.psapi.GetProcessMemoryInfo(self.handle, ctypes.byref(process), process.cb):
            raise ctypes.WinError(ctypes.get_last_error())
        return {
            "utc": utc_now(),
            "processCommitBytes": process.PrivateUsage,
            "processWorkingSetBytes": process.WorkingSetSize,
            "systemCommitBytes": system.CommitTotal * system.PageSize,
            "systemCommitLimitBytes": system.CommitLimit * system.PageSize,
            "systemCommitAvailableBytes": max(0, system.CommitLimit - system.CommitTotal) * system.PageSize,
            "systemPhysicalAvailableBytes": system.PhysicalAvailable * system.PageSize,
        }


def read_bake_status(path: Path) -> dict:
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        # The editor rewrites status.json in place. An incomplete read is not a failure.
        return {}


def is_current_terminal(status: dict, started: datetime) -> bool:
    phase = str(status.get("phase", "")).lower()
    terminal = phase in TERMINAL_PHASES or "failed" in phase
    if not terminal or status.get("isRunning", False):
        return False
    try:
        written = datetime.fromisoformat(str(status["utc"]).replace("Z", "+00:00"))
    except (KeyError, ValueError):
        return False
    return written >= started


def write_atomic(path: Path, value: dict):
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    for attempt in range(5):
        try:
            os.replace(temporary, path)
            return
        except PermissionError:
            if attempt == 4:
                raise
            time.sleep(0.05 * (attempt + 1))


def cancel_native(project: Path, pid: int, python: Path, output: Path) -> dict:
    # Check both project and process inside Unity before invoking any mutation.
    code = (
        'if (UnityEngine.Application.dataPath.Replace("\\\\", "/") != '
        '"D:/UnityProject/MaxSRP/Assets") throw new System.Exception("Wrong Unity project; no action."); '
        f'if (System.Diagnostics.Process.GetCurrentProcess().Id != {pid}) '
        'throw new System.Exception("Wrong Unity PID; no action."); '
        'if (!UnityEditor.Lightmapping.isRunning) return "NATIVE_NOT_RUNNING; no action."; '
        'MaxSRP.EditorTools.MaxBistroLightmapBaker.Cancel(); '
        'return "NATIVE_CANCEL_REQUESTED_LOW_SYSTEM_COMMIT";'
    )
    command = [str(python), str(project / "Tools/UnityMcp/client.py"), "call", "execute_code",
               json.dumps({"action": "execute", "code": code})]
    try:
        result = subprocess.run(command, cwd=project, capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=60,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        # Keep the detailed MCP result local; expose only exit status in the small status file.
        output.write_text(result.stdout + "\n" + result.stderr, encoding="utf-8")
        return {"attempted": True, "exitCode": result.returncode, "log": str(output)}
    except subprocess.TimeoutExpired:
        output.write_text("Cancellation MCP client timed out after 60 seconds; no retry.\n", encoding="utf-8")
        return {"attempted": True, "timedOut": True, "log": str(output)}
    except OSError as error:
        output.write_text(str(error) + "\n", encoding="utf-8")
        return {"attempted": True, "error": str(error), "log": str(output)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pid", type=int)
    parser.add_argument("--project", type=Path)
    parser.add_argument("--run-id")
    parser.add_argument("--python", type=Path)
    parser.add_argument("--interval", type=float, default=5.0)
    parser.add_argument("--sample-only", action="store_true")
    args = parser.parse_args()
    if args.sample_only:
        reader = WindowsMemory(args.pid or os.getpid())
        try:
            print(json.dumps({"pid": args.pid or os.getpid(), "image": reader.image_name(),
                              **reader.sample()}, indent=2))
        finally:
            reader.close()
        return 0
    if any(value is None for value in (args.pid, args.project, args.run_id, args.python)):
        parser.error("--pid, --project, --run-id and --python are required for monitoring.")
    if args.pid <= 0 or not 5.0 <= args.interval <= 10.0:
        parser.error("PID must be positive; --interval must be 5 to 10 seconds.")
    project = args.project.resolve(strict=True)
    if os.path.normcase(str(project)) != os.path.normcase(str(EXPECTED_PROJECT.resolve(strict=True))):
        parser.error("Only D:/UnityProject/MaxSRP is supported.")
    python = args.python.resolve(strict=True)
    if not python.is_file() or not (project / "Tools/UnityMcp/client.py").is_file():
        parser.error("MCP Python or existing Unity client is missing.")
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", args.run_id):
        parser.error("--run-id must contain 1 to 80 letters, digits, underscores or hyphens.")
    folder = project / "OfflineBaseline/BistroLightmap"
    folder.mkdir(parents=True, exist_ok=True)
    samples_path = folder / f"memory_watchdog_{args.run_id}.jsonl"
    status_path = folder / "memory_watchdog.json"
    cancel_log = folder / f"memory_watchdog_cancel_{args.run_id}.txt"
    started = datetime.now(timezone.utc)
    state = {"runId": args.run_id, "pid": args.pid, "startedUtc": utc_now(),
             "project": str(project), "lowCommitThresholdBytes": LOW_COMMIT_BYTES,
             "sampleLog": str(samples_path), "cancellation": {"attempted": False}}
    reader = None
    try:
        reader = WindowsMemory(args.pid)
        if Path(reader.image_name()).name.lower() != "unity.exe":
            raise RuntimeError("Explicit PID is not a Unity.exe process; no action.")
        consecutive_low = 0
        with samples_path.open("a", encoding="utf-8", buffering=1) as samples:
            while True:
                if not reader.alive():
                    state.update(utc=utc_now(), phase="process-exited")
                    break
                bake = read_bake_status(folder / "status.json")
                if is_current_terminal(bake, started):
                    state.update(utc=utc_now(), phase="bake-terminal", bakePhase=bake.get("phase"))
                    break
                sample = reader.sample()
                low = sample["systemCommitAvailableBytes"] < LOW_COMMIT_BYTES
                consecutive_low = consecutive_low + 1 if low else 0
                sample.update(pid=args.pid, runId=args.run_id,
                              bakePhase=bake.get("phase"), bakeRunning=bake.get("isRunning"),
                              consecutiveLowCommitSamples=consecutive_low)
                samples.write(json.dumps(sample) + "\n")
                state.update(utc=sample["utc"], phase="monitoring", lastSample=sample)
                write_atomic(status_path, state)
                if consecutive_low >= 2:
                    state.update(utc=utc_now(), phase="cancelling-low-commit")
                    write_atomic(status_path, state)
                    state["cancellation"] = cancel_native(project, args.pid, python, cancel_log)
                    state.update(utc=utc_now(), phase="cancel-attempt-finished")
                    # A timed-out call may still execute in Unity. Never send it twice.
                    break
                time.sleep(args.interval)
    except Exception as error:
        state.update(utc=utc_now(), phase="watchdog-error", error=str(error))
        return 1
    finally:
        if reader is not None:
            reader.close()
        write_atomic(status_path, state)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
