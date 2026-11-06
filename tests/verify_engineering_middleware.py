"""Verify the app-only Rockwell rollout using a disposable saved export and owned stand-in."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import xml.etree.ElementTree as ET


def run(arguments, code=0):
    process = subprocess.run(arguments, capture_output=True, text=True, encoding="utf-8",
                             timeout=45, creationflags=subprocess.CREATE_NO_WINDOW)
    assert process.returncode == code, (process.args, process.returncode, process.stdout, process.stderr)
    return json.loads(process.stdout)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--deployment", type=Path, required=True)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True, help="New directory for the disposable run")
    args = parser.parse_args()
    assert os.name == "nt" and all(path.is_absolute() for path in (args.deployment, args.fixture, args.evidence))
    args.evidence.mkdir()
    cli = args.deployment / "cli/Rockwell.EngineeringCli.exe"
    assert cli.is_file() and args.fixture.is_file() and not (cli.parent / "native-pilot.json").exists()
    fixture = Path(__file__).resolve().parents[1] / "examples/ReviewDemo.L5X"
    source = args.evidence / "source.L5X"
    source.write_bytes(fixture.read_bytes())
    original = sha(source)

    def call(operation, name, request, code=0):
        path = args.evidence / (name + ".json")
        path.write_text(json.dumps(request), encoding="utf-8")
        return run([cli, operation, "--request", path], code)

    with subprocess.Popen([args.fixture, "legacy-holder"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW) as holder:
        ready = queue.Queue()
        threading.Thread(target=lambda: ready.put(holder.stdout.readline()), daemon=True).start()
        try:
            assert ready.get(timeout=15).strip() == "ready"
            tags = call("l5x-inspect", "tags", {"sourcePath": str(source), "kind": "tags", "program": "ExampleProgram"})
        finally:
            holder.stdin.close()
            try:
                assert holder.wait(timeout=10) == 0
            except subprocess.TimeoutExpired:
                holder.terminate()
                holder.wait(timeout=10)
                raise
    assert tags["version"] == "1.1.0" and tags["cleanup"] == "confirmed" and tags["result"]["input"]["sha256"] == original
    selected = next(row for row in tags["result"]["items"] if row["name"] == "Input")["editableTexts"][0]
    rungs = call("l5x-inspect", "rungs", {"sourcePath": str(source), "kind": "rungs",
                                        "program": "ExampleProgram", "routine": "MainRoutine"})
    assert rungs["result"]["total"] == 3
    text = "Reviewed input for offline handoff"
    preview = call("l5x-preview", "preview-request", {"sourcePath": str(source), "expectedSha256": original,
                                                      "xpath": selected["xpath"], "text": text})
    review = preview["result"]["reviewFile"]
    assert preview["result"]["review"]["before"] == selected["text"] and sha(source) == original
    output = args.evidence / "reviewed.L5X"
    request = {"reviewPath": review["path"], "reviewSha256": review["sha256"], "outputPath": str(output)}
    stale = call("l5x-apply", "stale-review", {**request, "reviewSha256": "0" * 64}, 1)
    assert stale["error"]["code"] == "stale_review" and not output.exists()
    applied = call("l5x-apply", "apply-request", request)
    assert applied["result"]["readback"]["text"] == text and applied["result"]["output"]["sha256"] == sha(output)
    assert sha(source) == sha(fixture) == original
    original_tree, output_tree = ET.parse(source), ET.parse(output)
    selector = "./Controller/Programs/Program/Tags/Tag[@Name='Input']/Description"
    assert output_tree.findtext(selector) == text
    output_tree.find(selector).text = original_tree.findtext(selector)
    assert ET.tostring(output_tree.getroot()) == ET.tostring(original_tree.getroot())

    acd = args.evidence / "fingerprint-only.ACD"
    acd.write_bytes(b"Synthetic bytes for the no-SDK plan check")
    sdk_request = {"sourcePath": str(acd), "expectedSha256": sha(acd), "program": "NativeLocalProbe",
                   "tag": "PilotInput", "dataType": "DINT", "exportName": "review.L5X"}
    plan = call("sdk-read-export-plan", "sdk-plan-request", sdk_request)
    assert [step["method"] for step in plan["result"]["calls"]] == ["OpenLogixProjectAsync", "GetTagValueDINTAsync", "SaveAsAsync"]
    assert plan["result"]["nativeExecuted"] is False
    disabled = call("sdk-read-export", "disabled-sdk-request", sdk_request, 69)
    assert disabled["error"]["code"] == "native_disabled" and disabled["cleanup"] == "not-started"

    cleanup_root = args.evidence / "owned-cleanup"
    cleanup_root.mkdir()
    cleanup = run([args.fixture, "cleanup", cleanup_root])
    assert cleanup["passed"]
    report = {"version": tags["version"], "tags": tags, "rungs": rungs, "preview": preview, "apply": applied,
              "staleReview": stale, "legacyMutexDidNotRejectFileJob": True, "sourceAndOtherXmlPreserved": True,
              "sdkPlan": plan, "nativeDisabled": disabled, "cancelledOwnedTree": cleanup}
    with (args.evidence / "smoke.json").open("x", encoding="utf-8") as output_file:
        json.dump(report, output_file, indent=2)
        output_file.write("\n")
    print(json.dumps({"version": tags["version"], "output": applied["result"]["output"], "sourceSha256": original,
                      "receipt": str(args.evidence / "smoke.json"), "cleanup": cleanup}))


if __name__ == "__main__":
    main()
