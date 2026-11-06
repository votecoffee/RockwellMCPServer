"""Run the Windows CLI, optional MCP adapter, and process-ownership smoke checks."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import threading


def run(arguments, code=0):
    process = subprocess.run(arguments, capture_output=True, text=True, encoding="utf-8",
                             timeout=45, creationflags=subprocess.CREATE_NO_WINDOW)
    assert process.returncode == code, (process.args, process.returncode, process.stdout, process.stderr)
    return json.loads(process.stdout)


def receive_lines(stream, output):
    for line in stream:
        output.put(line)
    output.put(None)


def finish_fixture(process, seconds):
    process.stdin.close()
    try:
        assert process.wait(timeout=seconds) == 0
    except subprocess.TimeoutExpired:
        process.terminate()
        process.wait(timeout=10)
        raise


def adapter_under_contention(executable, cli, source):
    messages = queue.Queue()
    with subprocess.Popen([executable, "--cli", cli], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          stderr=subprocess.DEVNULL, text=True, encoding="utf-8",
                          creationflags=subprocess.CREATE_NO_WINDOW) as process:
        threading.Thread(target=receive_lines, args=(process.stdout, messages), daemon=True).start()

        def rpc(method, params, identifier=None):
            request = {"jsonrpc": "2.0", "method": method, "params": params}
            if identifier is not None:
                request["id"] = identifier
            process.stdin.write(json.dumps(request) + "\n")
            process.stdin.flush()
            if identifier is not None:
                reply = json.loads(messages.get(timeout=30))
                assert reply["id"] == identifier and "error" not in reply, reply
                return reply["result"]

        try:
            rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                               "clientInfo": {"name": "middleware-smoke", "version": "1"}}, 1)
            rpc("notifications/initialized", {})
            tools = rpc("tools/list", {}, 2)["tools"]
            assert [tool["name"] for tool in tools] == ["rockwell_l5x_summary"]
            result = rpc("tools/call", {"name": tools[0]["name"], "arguments": {"path": str(source)}}, 3)
            payload = result.get("structuredContent") or json.loads(result["content"][0]["text"])
            assert payload["error"]["code"] == "busy" and payload["exitCode"] == 75, result
        finally:
            finish_fixture(process, 15)
    return len(tools)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--deployment", type=Path, required=True)
    parser.add_argument("--fixture", type=Path, required=True, help="Built Windows middleware fixture executable")
    parser.add_argument("--evidence", type=Path, required=True, help="New directory for this run")
    args = parser.parse_args()
    assert os.name == "nt" and all(path.is_absolute() for path in (args.deployment, args.fixture, args.evidence))
    args.evidence.mkdir()
    cli = args.deployment / "cli/Rockwell.EngineeringCli.exe"
    adapter = args.deployment / "mcp-adapter/Rockwell.EngineeringMcp.exe"
    assert cli.is_file() and adapter.is_file() and args.fixture.is_file()
    source = Path(__file__).resolve().parents[1] / "examples/Demo.L5X"
    state = Path(os.environ["LOCALAPPDATA"]) / "RockwellEngineering/jobs"

    def job_ids():
        return set(state.iterdir()) if state.is_dir() else set()

    summary = run([cli, "l5x-summary", "--input", source])
    assert summary["cleanup"] == "confirmed" and summary["result"]["input"]["sha256"] == hashlib.sha256(source.read_bytes()).hexdigest()
    invalid = args.evidence / "invalid.L5X"
    invalid.write_bytes(b"not XML")
    failure = run([cli, "l5x-summary", "--input", invalid], 1)
    assert failure["cleanup"] == "confirmed" and failure["error"]["code"] == "invalid_xml"
    disabled = run([cli, "sdk-offline", "--request", args.evidence / "unused.json"], 69)
    assert disabled["cleanup"] == "not-started" and disabled["error"]["code"] == "native_disabled"
    catalog = run([cli, "sdk-catalog"])
    assert catalog["methods"] and "GetProcessorTypesAsync" in catalog["offlinePilotMethods"]

    before = job_ids()
    with subprocess.Popen([args.fixture, "holder"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW) as holder:
        lines = queue.Queue()
        threading.Thread(target=receive_lines, args=(holder.stdout, lines), daemon=True).start()
        try:
            assert (lines.get(timeout=15) or "").strip() == "ready", "The shared mutex must be available for this control."
            busy = run([cli, "l5x-summary", "--input", source], 75)
            assert busy["error"]["code"] == "busy" and busy["jobId"] is None and busy["cleanup"] == "not-started"
            assert run([cli, "operations"])["operations"]
            tool_count = adapter_under_contention(adapter, cli, source)
            assert job_ids() == before
        finally:
            finish_fixture(holder, 10)
    cleanup_root = args.evidence / "owned-cleanup"
    cleanup_root.mkdir()
    cleanup = run([args.fixture, "cleanup", cleanup_root])
    assert cleanup["passed"]
    report = {"summary": summary, "failure": failure, "nativeDisabled": disabled,
              "sdkCatalogMethods": len(catalog["methods"]), "busy": busy,
              "adapterDiscoveryTools": tool_count, "discoveryAndBusyCreatedNoJob": True, "cancelledOwnedTree": cleanup}
    with (args.evidence / "smoke.json").open("x", encoding="utf-8") as output:
        json.dump(report, output, indent=2)
        output.write("\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
