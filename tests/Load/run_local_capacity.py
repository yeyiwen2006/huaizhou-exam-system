"""Run real HTTP/SignalR local capacity scenarios using only Python's standard library.

Use a newly provisioned synthetic fixture. This coordinator starts a separate
server process; it neither mocks APIs nor certifies physical/dual-host capacity.
"""
from __future__ import annotations
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import time
import urllib.request


def load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def save(path: Path, data: object) -> None:
    with path.open("x", encoding="utf-8") as out:
        json.dump(data, out, ensure_ascii=False, indent=2)


def flatten(settings: dict, prefix: str = "") -> dict[str, str]:
    result = {}
    for key, value in settings.items():
        name = prefix + "__" + key if prefix else key
        if isinstance(value, dict):
            result.update(flatten(value, name))
        elif isinstance(value, list):
            result.update(flatten({str(i): v for i, v in enumerate(value)}, name))
        else:
            result[name] = str(value).lower() if isinstance(value, bool) else str(value)
    return result


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        raise ValueError("Local test endpoints must not redirect")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--duration-seconds", type=int, default=120)
    parser.add_argument("--port", type=int, default=5091)
    args = parser.parse_args()
    if not 1 <= args.duration_seconds <= 86400 or not 1024 <= args.port <= 65535:
        parser.error("Duration must be 1..86400 seconds and port 1024..65535")
    repo = Path(__file__).resolve().parents[2]
    fixture, report = args.fixture.resolve(strict=True), args.report.resolve()
    if report.exists():
        parser.error("Report directory must be new; existing results are never overwritten")
    configuration = load(fixture / "local-server.json")
    manifest = load(fixture / "sessions.json")
    setup = load(fixture / "setup.json")
    if setup.get("synthetic") is not True or manifest.get("synthetic") is not True or not re.fullmatch(r"exam_load_[a-f0-9]{32}", setup.get("database", "")):
        parser.error("Only new isolated synthetic fixtures are accepted")
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise RuntimeError("dotnet was not found")
    setup_dll = repo / "tests/Load/Exam.LoadSetup/bin/Release/net10.0/Exam.LoadSetup.dll"
    server_dll = repo / "src/Exam.Server/bin/Release/net10.0/Exam.Server.dll"
    load_dll = repo / "tests/Load/Exam.LoadTests/bin/Release/net10.0/Exam.LoadTests.dll"
    if any(not p.is_file() for p in (setup_dll, server_dll, load_dll)):
        raise RuntimeError("Build the Release solution before running capacity scenarios")
    # The .NET validator checks the actual connection-string host/database against
    # the fixture and refuses any database that already contains answers.
    subprocess.run([dotnet, str(setup_dll), "--validate-fixture", str(fixture)], cwd=repo, check=True, timeout=30)
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", args.port))
    report.mkdir(parents=True)
    # Do not inherit configuration overrides from a production shell.
    inherited = {"systemroot", "windir", "path", "pathext", "temp", "tmp", "comspec", "userprofile", "appdata", "localappdata", "programfiles", "programfiles(x86)", "programdata", "systemdrive", "dotnet_root"}
    environment = {k: v for k, v in os.environ.items() if k.lower() in inherited}
    environment.update(flatten(configuration))
    environment.update(ASPNETCORE_ENVIRONMENT="Development", DOTNET_ENVIRONMENT="Development", Database__ApplyMigrations="true", Logging__LogLevel__Default="Warning", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    target = f"http://127.0.0.1:{args.port}"
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    # 夹具登录合成考务账号后保存的会话令牌（sessions.json 为私有、被忽略的文件）。
    token = manifest["operatorToken"]
    def request(path: str, authenticated: bool = False) -> dict:
        headers = {"Authorization": "Bearer " + token} if authenticated else {}
        with opener.open(urllib.request.Request(target + path, headers=headers), timeout=3) as response:
            return json.load(response)
    started = time.monotonic()
    results = []
    server = None
    overall_error = None
    output = (report / "server.stdout.log").open("wb")
    error = (report / "server.stderr.log").open("wb")
    try:
        server = subprocess.Popen([dotnet, str(server_dll), "--urls", target], cwd=repo, env=environment, stdout=output, stderr=error)
        ready = False
        for _ in range(120):
            if server.poll() is not None:
                raise RuntimeError("Isolated server exited before readiness; inspect its private log")
            try:
                health = request("/health/ready")
                ready = health.get("status") == "ready" and health.get("serverId") == manifest["serverId"] and health.get("nodeId") == configuration["ExamServer"]["NodeId"] and health.get("serverEpoch") == manifest["serverEpoch"]
            except Exception:
                pass
            if ready:
                break
            time.sleep(.5)
        if not ready:
            raise RuntimeError("Isolated server did not pass identity/readiness checks")
        sessions = manifest["sessions"]
        plans = [("burst", count, f"burst-{count}") for count in (10, 50, 100, 200, 300) if count <= len(sessions)]
        plans.append(("mixed", len(sessions), "mixed"))
        for index, (scenario, count, label) in enumerate(plans, 1):
            chosen = json.loads(json.dumps(manifest))
            chosen["sessions"] = chosen["sessions"][:count]
            for session in chosen["sessions"]:
                session["initialAnswerVersion"] = index * 100000
            session_file = report / (label + ".sessions.json")
            save(session_file, chosen)  # Private synthetic credentials; never commit the report folder.
            observations = []
            arguments = [dotnet, str(load_dll), "--target", target, "--sessions", str(session_file), "--scenario", scenario, "--output", str(report / label), "--confirm-synthetic", "--allow-loopback-http", "--duration-seconds", str(args.duration_seconds)]
            with (report / (label + ".stdout.log")).open("wb") as out, (report / (label + ".stderr.log")).open("wb") as err:
                process = subprocess.Popen(arguments, cwd=repo, stdout=out, stderr=err)
                deadline = time.monotonic() + args.duration_seconds + 600
                try:
                    while process.poll() is None:
                        try:
                            observations.append(request("/api/v1/diagnostics/runtime", True)["result"])
                        except Exception:
                            observations.append({"sampledAt": datetime.now(timezone.utc).isoformat(), "unavailable": True})
                        if time.monotonic() >= deadline:
                            raise TimeoutError("Load runner exceeded its bounded scenario time")
                        time.sleep(2)
                finally:
                    if process.poll() is None:
                        process.terminate()
                        process.wait(timeout=15)
                    save(report / (label + ".observations.json"), observations)
            results.append({"scenario": scenario, "count": count, "label": label, "exitCode": process.returncode})
            print(f"{label}: exit={process.returncode}", flush=True)
            if process.returncode not in (0, 1):
                raise RuntimeError("Load scenario could not run; inspect its private log")
        save(report / "server-diagnostics.json", request("/api/v1/diagnostics/runtime", True))
        save(report / "collection-events.json", request("/api/v1/submissions/collection/events?examId=" + setup["examId"] + "&afterRosterVersion=0", True))
        audit = subprocess.run([dotnet, str(setup_dll), "--audit", str(fixture), str(report / "database-audit.json")], cwd=repo, timeout=30)
        results.append({"scenario": "database-audit", "count": len(sessions), "exitCode": audit.returncode})
    except Exception as exc:
        overall_error = type(exc).__name__  # Do not leak token-bearing request details into a summary.
        raise
    finally:
        if server is not None and server.poll() is None:
            server.terminate()
            server.wait(timeout=15)
        output.close(); error.close()
        save(report / "local-run.json", {"synthetic": True, "productionCapacityCertified": False, "elapsedSeconds": time.monotonic()-started, "errorType": overall_error, "environment": {"os": os.name, "logicalProcessors": os.cpu_count(), "sameMachineGeneratorAndServer": True, "synchronousReplication": False, "transport": "Explicit HTTP loopback test exception", "diagnosticsSamplingSeconds": 2}, "scenarios": results})
    return 1 if any(r["exitCode"] != 0 for r in results) else 0


if __name__ == "__main__":
    raise SystemExit(main())
