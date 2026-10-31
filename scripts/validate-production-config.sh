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
    r"(?m)^AuthAltParameters=.*jwks=.*disable_token_creation.*$",
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
    if "SuspendProgram=" not in slurm or "lifecycle-client.conf" not in actual:
        raise SystemExit("power saving requires both hooks and lifecycle client config")
elif "lifecycle-client.conf" in actual:
    raise SystemExit("lifecycle client config exists while power saving is disabled")

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
