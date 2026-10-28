#!/usr/bin/env python3
"""Small host client for the JWT-protected FinSimProxy REST API."""

from __future__ import annotations

import argparse
import http.client
import json
import os
from pathlib import Path
import stat
import time
import uuid


def read_token(path: Path) -> str:
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode) or not stat.S_ISREG(info.st_mode):
        raise ValueError("API token must be a regular non-symlink file")
    if info.st_mode & 0o077:
        raise ValueError("API token permissions are too broad")
    if info.st_size <= 0 or info.st_size > 16384:
        raise ValueError("API token has an invalid size")
    token = path.read_text(encoding="ascii").strip()
    if not token or "\n" in token or "\r" in token:
        raise ValueError("API token content is invalid")
    return token


def request(
    method: str,
    path: str,
    token: str,
    host: str,
    port: int,
    body: dict[str, object] | None = None,
    idempotency_key: str | None = None,
) -> tuple[int, dict[str, object]]:
    connection = http.client.HTTPConnection(host, port, timeout=15)
    headers = {
        "Accept": "application/json",
        "Authorization": f"Bearer {token}",
        "X-Correlation-ID": str(uuid.uuid4()),
    }
    encoded = None
    if body is not None:
        encoded = json.dumps(body, separators=(",", ":")).encode("ascii")
        headers["Content-Type"] = "application/json"
    if idempotency_key:
        headers["Idempotency-Key"] = idempotency_key
    connection.request(method, path, encoded, headers)
    response = connection.getresponse()
    payload = response.read(1024 * 1024 + 1)
    connection.close()
    if len(payload) > 1024 * 1024:
        raise ValueError("proxy response is too large")
    parsed = json.loads(payload) if payload else {}
    if not isinstance(parsed, dict):
        raise ValueError("proxy response is not an object")
    return response.status, parsed


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--count", type=int, default=20)
    parser.add_argument("--chunk-size", type=int, default=4)
    parser.add_argument("--parallel", type=int, default=3)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--host", default=os.environ.get("FINSIM_PROXY_HOST", "127.0.0.1"))
    parser.add_argument(
        "--port", type=int, default=int(os.environ.get("FINSIM_PROXY_PORT", "8088"))
    )
    parser.add_argument("--idempotency-key", default=f"local-{uuid.uuid4()}")
    parser.add_argument("--wait", action="store_true")
    args = parser.parse_args()
    if not 1 <= args.count <= 10000:
        parser.error("--count must be between 1 and 10000")
    token_path = Path(__file__).resolve().parents[1] / ".runtime/secrets/api-client.jwt"
    token = read_token(token_path)
    status, created = request(
        "POST",
        "/v1/simulations/",
        token,
        args.host,
        args.port,
        {
            "values": list(range(1, args.count + 1)),
            "chunkSize": args.chunk_size,
            "maxParallel": args.parallel,
        },
        args.idempotency_key,
    )
    if status != 202:
        raise SystemExit(f"submission returned HTTP {status}: {created.get('error', 'rejected')}")
    workflow_id = created["workflowId"]
    print(f"workflow_id={workflow_id} status={created['status']}")
    if not args.wait:
        return 0

    deadline = time.monotonic() + args.timeout
    while time.monotonic() < deadline:
        status, snapshot = request(
            "GET", f"/v1/simulations/{workflow_id}", token, args.host, args.port
        )
        if status != 200:
            raise SystemExit(f"status request returned HTTP {status}")
        state = snapshot.get("status")
        print(f"status={state}")
        if state == "SUCCEEDED":
            report_status, report = request(
                "GET",
                f"/v1/simulations/{workflow_id}/report",
                token,
                args.host,
                args.port,
            )
            if report_status != 200:
                raise SystemExit(f"report request returned HTTP {report_status}")
            print(json.dumps(report, sort_keys=True, indent=2))
            return 0
        if state in {"FAILED", "CANCELLED", "NEEDS_ATTENTION"}:
            raise SystemExit(f"workflow ended in {state}")
        time.sleep(2)
    raise SystemExit("workflow polling timed out")


if __name__ == "__main__":
    raise SystemExit(main())
