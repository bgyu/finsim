#!/usr/bin/env bash
# Validate a rendered production bundle before it is distributed to hosts.
set -Eeuo pipefail

[[ $# -eq 1 ]] || {
    printf 'usage: %s CONFIG_DIRECTORY\n' "$0" >&2
    exit 64
}
directory=$(realpath -e -- "$1") || exit 78
[[ -d $directory && ! -L $directory ]] || {
    printf 'validate-production-config: input must be a directory\n' >&2
    exit 78
}

python3 - "$directory" <<'PY'
from pathlib import Path
import hashlib, json, re, stat, sys

root = Path(sys.argv[1])
required = {
    "slurm.conf", "cgroup.conf", "job_container.conf",
    "slurmdbd.conf.template", "mariadb-server.cnf", "generated-manifest.json"
}
actual = {path.name for path in root.iterdir() if path.is_file()}
missing = required - actual
if missing:
    raise SystemExit(f"missing production configuration: {sorted(missing)}")

manifest = json.loads((root / "generated-manifest.json").read_text(encoding="utf-8"))
if manifest.get("schema") != 1 or manifest.get("profile") != "production":
    raise SystemExit("generated manifest is not a production schema-1 bundle")
files = manifest.get("files")
if not isinstance(files, dict) or set(files) != actual - {"generated-manifest.json"}:
    raise SystemExit("generated manifest file set does not match the bundle")
for name, digest in files.items():
    path = root / name
    if path.is_symlink() or not path.is_file():
        raise SystemExit(f"invalid generated file: {name}")
    if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
        raise SystemExit(f"generated file digest mismatch: {name}")
    if path.stat().st_mode & (stat.S_IWGRP | stat.S_IWOTH):
        raise SystemExit(f"generated file is group/world writable: {name}")

slurm = (root / "slurm.conf").read_text(encoding="utf-8")
checks = (
    r"(?m)^DisableRootJobs=YES$",
    r"(?m)^AuthAltParameters=.*jwks=.*use_jwt_client_ids_only.*disable_token_creation.*$",
    r"(?m)^TLSType=tls/s2n$",
    r"(?m)^ProctrackType=proctrack/cgroup$",
    r"(?m)^TaskPlugin=.*task/cgroup.*$",
    r"(?m)^PrivateData=.*jobs.*$",
)
for pattern in checks:
    if not re.search(pattern, slurm):
        raise SystemExit(f"production slurm.conf fails invariant: {pattern}")
if slurm.count("SlurmctldHost=") != 2:
    raise SystemExit("production requires exactly two SlurmctldHost records")
if "ResumeProgram=" in slurm:
    if ("SuspendProgram=" not in slurm or "lifecycle-client.conf" not in actual or
            "cloud-nodes.conf" not in actual):
        raise SystemExit(
            "power saving requires both hooks, lifecycle config, and cloud inventory"
        )
elif "lifecycle-client.conf" in actual:
    raise SystemExit("lifecycle client config exists while power saving is disabled")

dbd = (root / "slurmdbd.conf.template").read_text(encoding="utf-8")
if not re.search(
    r"(?m)^AuthAltParameters=.*jwks=.*use_jwt_client_ids_only.*disable_token_creation.*$",
    dbd,
):
    raise SystemExit("production slurmdbd JWT identity policy is incomplete")

cloud_path = root / "cloud-nodes.conf"
if cloud_path.exists():
    if cloud_path.is_symlink():
        raise SystemExit("cloud inventory must not be a symlink")
    records = []
    seen = set()
    node_pattern = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.\[\],-]{0,255}")
    for number, original in enumerate(cloud_path.read_text(encoding="ascii").splitlines(), 1):
        line = original.strip()
        if not line or line.startswith("#"):
            continue
        values = {}
        for token in line.split():
            if token.count("=") != 1:
                raise SystemExit(f"invalid cloud inventory token on line {number}")
            name, value = token.split("=", 1)
            if name in values:
                raise SystemExit(f"duplicate cloud inventory key on line {number}")
            values[name] = value
        if set(values) != {"NodeName", "CPUs", "RealMemory", "Feature", "State"}:
            raise SystemExit(f"invalid cloud inventory fields on line {number}")
        if values["State"] != "CLOUD" or values["Feature"] not in {
            "finsim-master", "finsim-worker"
        }:
            raise SystemExit(f"invalid cloud inventory policy on line {number}")
        if not node_pattern.fullmatch(values["NodeName"]):
            raise SystemExit(f"invalid cloud inventory NodeName on line {number}")
        if not re.fullmatch(r"[1-9][0-9]{0,9}", values["CPUs"]) or not re.fullmatch(
            r"[1-9][0-9]{0,9}", values["RealMemory"]
        ):
            raise SystemExit(f"invalid cloud inventory resources on line {number}")
        if int(values["CPUs"]) > 8192 or int(values["RealMemory"]) > 1_000_000_000:
            raise SystemExit(f"cloud inventory resources exceed bounds on line {number}")
        if values["NodeName"] in seen:
            raise SystemExit("duplicate cloud inventory NodeName expression")
        seen.add(values["NodeName"])
        records.append(line)
    if not records:
        raise SystemExit("cloud inventory contains no NodeName records")
    for record in records:
        if slurm.count(record) != 1:
            raise SystemExit("slurm.conf does not contain the exact cloud inventory")
elif re.search(r"(?m)^NodeName=", slurm):
    raise SystemExit("slurm.conf contains unmanifested cloud inventory")

cgroup = (root / "cgroup.conf").read_text(encoding="utf-8")
for setting in ("ConstrainCores=yes", "ConstrainRAMSpace=yes",
                "ConstrainSwapSpace=yes", "ConstrainDevices=yes"):
    if setting not in cgroup:
        raise SystemExit(f"production cgroup setting is missing: {setting}")

mariadb = (root / "mariadb-server.cnf").read_text(encoding="utf-8")
for setting in ("ssl_ca=", "ssl_cert=", "ssl_key=", "require_secure_transport=ON"):
    if setting not in mariadb:
        raise SystemExit(f"production MariaDB TLS setting is missing: {setting}")

for path in root.iterdir():
    if not path.is_file():
        continue
    content = path.read_bytes()
    if path.name == "slurmdbd.conf.template":
        # These five values are intentionally injected into private tmpfs by
        # the slurmdbd entrypoint after reading its role-filtered credentials.
        content = re.sub(rb"@STORAGE_[A-Z_]+@", b"runtime-value", content)
    if re.search(rb"@[A-Z_]+@", content):
        raise SystemExit(f"unresolved template marker in {path.name}")
PY

printf 'Production configuration is valid: %s\n' "$directory"
