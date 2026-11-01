"""Tests for reusable package build/install/configuration interfaces."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
COMMON = ROOT / "scripts" / "lib" / "build-common.sh"
RUNTIME_ENV = ROOT / "scripts" / "lib" / "runtime-env.sh"


def run_bash(program: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["bash", "-c", program],
        cwd=ROOT,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )


class BuildScriptTests(unittest.TestCase):
    @staticmethod
    def production_environment(**overrides: str) -> dict[str, str]:
        environment = os.environ.copy()
        environment.update(
            {
                "FINSIM_CONTROLLER_PRIMARY": "controller-primary",
                "FINSIM_CONTROLLER_BACKUP": "controller-backup",
                "FINSIM_SLURMDBD_PRIMARY": "slurmdbd-primary",
                "FINSIM_SLURMDBD_BACKUP": "slurmdbd-backup",
            }
        )
        environment.update(overrides)
        return environment

    def test_runtime_environment_is_parsed_as_data(self) -> None:
        with tempfile.TemporaryDirectory(dir="/tmp") as temporary:
            root = Path(temporary)
            environment = root / "cluster.env"
            environment.write_text(
                "FINSIM_IMAGE_TAG=review\n"
                "FINSIM_APP_RELEASE=2026.10.31.1\n"
                "FINSIM_PROXY_PORT=18088\n",
                encoding="ascii",
            )
            environment.chmod(0o600)
            result = run_bash(
                f"source {RUNTIME_ENV!s}; runtime_env_load {environment!s}; "
                "printf '%s|%s|%s\\n' \"$FINSIM_IMAGE_TAG\" "
                "\"$FINSIM_APP_RELEASE\" \"$FINSIM_PROXY_PORT\""
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout.strip(), "review|2026.10.31.1|18088")

    def test_runtime_environment_rejects_shell_syntax_without_execution(self) -> None:
        with tempfile.TemporaryDirectory(dir="/tmp") as temporary:
            root = Path(temporary)
            marker = root / "executed"
            environment = root / "cluster.env"
            environment.write_text(
                f"FINSIM_IMAGE_TAG=$(touch${{IFS}}{marker})\n"
                "FINSIM_APP_RELEASE=dev\n"
                "FINSIM_PROXY_PORT=8088\n",
                encoding="ascii",
            )
            environment.chmod(0o600)
            result = run_bash(
                f"source {RUNTIME_ENV!s}; runtime_env_load {environment!s}"
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse(marker.exists())

    def test_runtime_environment_rejects_duplicate_or_unknown_keys(self) -> None:
        for invalid_line in ("FINSIM_IMAGE_TAG=again", "UNEXPECTED=value"):
            with self.subTest(invalid_line=invalid_line), tempfile.TemporaryDirectory(
                dir="/tmp"
            ) as temporary:
                environment = Path(temporary) / "cluster.env"
                environment.write_text(
                    "FINSIM_IMAGE_TAG=review\n"
                    "FINSIM_APP_RELEASE=dev\n"
                    "FINSIM_PROXY_PORT=8088\n"
                    f"{invalid_line}\n",
                    encoding="ascii",
                )
                environment.chmod(0o600)
                result = run_bash(
                    f"source {RUNTIME_ENV!s}; runtime_env_load {environment!s}"
                )
                self.assertNotEqual(result.returncode, 0)

    def test_production_units_are_role_filtered_and_digest_pinned(self) -> None:
        digest = "registry.example.invalid/finsim/runtime@sha256:" + "a" * 64
        roles = (
            "database",
            "slurmdbd",
            "controller",
            "restd",
            "compute-master",
            "compute-worker",
            "proxy",
            "cache",
        )
        with tempfile.TemporaryDirectory(dir="/tmp") as temporary:
            for role in roles:
                instance = role.replace("-", "") + "01"
                command = [
                    "bash",
                    str(ROOT / "scripts" / "render-production-unit.sh"),
                    "--role",
                    role,
                    "--instance",
                    instance,
                    "--image",
                    digest,
                    "--network",
                    "finsim-routable",
                    "--output-dir",
                    temporary,
                ]
                if role in {"proxy", "cache"}:
                    command.extend(["--app-release", "2026.10.31.1"])
                rendered = subprocess.run(
                    command,
                    cwd=ROOT,
                    text=True,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    check=False,
                )
                self.assertEqual(rendered.returncode, 0, rendered.stderr)
                unit = Path(temporary) / f"finsim-{instance}.container"
                validated = subprocess.run(
                    ["bash", str(ROOT / "scripts" / "validate-production-unit.sh"), str(unit)],
                    cwd=ROOT,
                    text=True,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    check=False,
                )
                self.assertEqual(validated.returncode, 0, validated.stderr)
                text = unit.read_text(encoding="utf-8")
                self.assertNotIn("Volume=/etc/finsim/secrets:/run/secrets", text)
                self.assertNotIn("SYS_ADMIN", text)
                if role in {"compute-master", "compute-worker"}:
                    self.assertIn(
                        "PodmanArgs=--privileged --cgroupns=host", text
                    )
                    self.assertIn(
                        "Volume=/sys/fs/cgroup:/sys/fs/cgroup:rw", text
                    )
                    self.assertNotIn("NoNewPrivileges=true", text)
                else:
                    self.assertNotIn("--privileged", text)
                    self.assertNotIn("--cgroupns=host", text)
                    self.assertIn("NoNewPrivileges=true", text)
                    self.assertIn("DropCapability=all", text)
                if role in {"proxy", "cache"}:
                    self.assertIn("/health/ready", text)
                    self.assertIn("--cacert /run/secrets/service-ca-bundle.pem", text)

    def test_lifecycle_requires_and_manifests_cloud_inventory(self) -> None:
        with tempfile.TemporaryDirectory(dir="/tmp") as temporary:
            root = Path(temporary)
            output = root / "config"
            inventory = root / "cloud-nodes.conf"
            inventory.write_text(
                "# Bounded scheduler-visible capacity\n"
                "NodeName=master[0001-0010] CPUs=4 RealMemory=8192 "
                "Feature=finsim-master State=CLOUD\n"
                "NodeName=worker[0001-3000] CPUs=8 RealMemory=16384 "
                "Feature=finsim-worker State=CLOUD\n",
                encoding="ascii",
            )
            inventory.chmod(0o600)
            command = [
                "bash",
                str(ROOT / "scripts" / "prepare-config.sh"),
                "--profile",
                "production",
                "--output-dir",
                str(output),
                "--cloud-inventory",
                str(inventory),
            ]
            rendered = subprocess.run(
                command,
                cwd=ROOT,
                env=self.production_environment(
                    FINSIM_LIFECYCLE_ENABLE="1",
                    FINSIM_LIFECYCLE_API_URL="https://lifecycle.service.internal",
                ),
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=False,
            )
            self.assertEqual(rendered.returncode, 0, rendered.stderr)
            slurm = (output / "slurm.conf").read_text(encoding="utf-8")
            self.assertIn("NodeName=master[0001-0010]", slurm)
            self.assertIn("NodeName=worker[0001-3000]", slurm)
            manifest = json.loads(
                (output / "generated-manifest.json").read_text(encoding="utf-8")
            )
            self.assertIn("cloud-nodes.conf", manifest["files"])
            validated = subprocess.run(
                [
                    "bash",
                    str(ROOT / "scripts" / "validate-production-config.sh"),
                    str(output),
                ],
                cwd=ROOT,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=False,
            )
            self.assertEqual(validated.returncode, 0, validated.stderr)

            (output / "cloud-nodes.conf").write_text(
                "NodeName=tampered CPUs=1 RealMemory=1 Feature=finsim-worker State=CLOUD\n",
                encoding="ascii",
            )
            tampered = subprocess.run(
                [
                    "bash",
                    str(ROOT / "scripts" / "validate-production-config.sh"),
                    str(output),
                ],
                cwd=ROOT,
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=False,
            )
            self.assertNotEqual(tampered.returncode, 0)

    def test_lifecycle_rejects_missing_cloud_inventory(self) -> None:
        with tempfile.TemporaryDirectory(dir="/tmp") as temporary:
            result = subprocess.run(
                [
                    "bash",
                    str(ROOT / "scripts" / "prepare-config.sh"),
                    "--profile",
                    "production",
                    "--output-dir",
                    temporary,
                ],
                cwd=ROOT,
                env=self.production_environment(
                    FINSIM_LIFECYCLE_ENABLE="1",
                    FINSIM_LIFECYCLE_API_URL="https://lifecycle.service.internal",
                ),
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=False,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("cloud-inventory", result.stderr)

    def test_cloud_inventory_rejects_unsafe_or_invalid_data(self) -> None:
        invalid_documents = {
            "empty": "# no records\n",
            "unknown-key": (
                "PartitionName=batch Nodes=ALL State=UP\n"
            ),
            "missing-memory": (
                "NodeName=worker01 CPUs=8 Feature=finsim-worker State=CLOUD\n"
            ),
            "wrong-state": (
                "NodeName=worker01 CPUs=8 RealMemory=1024 "
                "Feature=finsim-worker State=FUTURE\n"
            ),
            "wrong-feature": (
                "NodeName=worker01 CPUs=8 RealMemory=1024 "
                "Feature=untrusted State=CLOUD\n"
            ),
            "duplicate": (
                "NodeName=worker01 CPUs=8 RealMemory=1024 "
                "Feature=finsim-worker State=CLOUD\n"
                "NodeName=worker01 CPUs=8 RealMemory=1024 "
                "Feature=finsim-worker State=CLOUD\n"
            ),
            "shell-syntax": (
                "NodeName=$(touch CPUs=8 RealMemory=1024 "
                "Feature=finsim-worker State=CLOUD\n"
            ),
        }
        for name, content in invalid_documents.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory(
                dir="/tmp"
            ) as temporary:
                root = Path(temporary)
                inventory = root / "inventory"
                inventory.write_text(content, encoding="ascii")
                inventory.chmod(0o600)
                result = subprocess.run(
                    [
                        "bash",
                        str(ROOT / "scripts" / "prepare-config.sh"),
                        "--profile",
                        "production",
                        "--output-dir",
                        str(root / "output"),
                        "--cloud-inventory",
                        str(inventory),
                    ],
                    cwd=ROOT,
                    env=self.production_environment(),
                    text=True,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    check=False,
                )
                self.assertNotEqual(result.returncode, 0, name)

        with tempfile.TemporaryDirectory(dir="/tmp") as temporary:
            root = Path(temporary)
            inventory = root / "inventory"
            inventory.write_text(
                "NodeName=worker01 CPUs=8 RealMemory=1024 "
                "Feature=finsim-worker State=CLOUD\n",
                encoding="ascii",
            )
            inventory.chmod(0o666)
            result = subprocess.run(
                [
                    "bash",
                    str(ROOT / "scripts" / "prepare-config.sh"),
                    "--profile",
                    "production",
                    "--output-dir",
                    str(root / "output"),
                    "--cloud-inventory",
                    str(inventory),
                ],
                cwd=ROOT,
                env=self.production_environment(),
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=False,
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("writable", result.stderr)

            inventory.chmod(0o600)
            link = root / "inventory-link"
            link.symlink_to(inventory)
            result = subprocess.run(
                [
                    "bash",
                    str(ROOT / "scripts" / "prepare-config.sh"),
                    "--profile",
                    "production",
                    "--output-dir",
                    str(root / "output-link"),
                    "--cloud-inventory",
                    str(link),
                ],
                cwd=ROOT,
                env=self.production_environment(),
                text=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=False,
            )
            self.assertNotEqual(result.returncode, 0)

    def test_pinned_version_file_is_parsed_as_data(self) -> None:
        result = run_bash(
            f"source {COMMON!s}; "
            f"build_load_versions {ROOT / 'build' / 'versions.conf'}; "
            "printf '%s|%s\n' \"$SLURM_VERSION\" \"$MUNGE_VERSION\""
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), "26.05.5|0.5.18")

    def test_version_parser_rejects_unknown_code_without_executing_it(self) -> None:
        with tempfile.TemporaryDirectory(dir="/tmp") as temporary:
            root = Path(temporary)
            marker = root / "executed"
            version_file = root / "versions.conf"
            version_file.write_text(
                "FORMAT_VERSION=1\n"
                f"EVIL=$(touch${{IFS}}{marker})\n",
                encoding="ascii",
            )
            result = run_bash(
                f"source {COMMON!s}; build_load_versions {version_file!s}"
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse(marker.exists())

    def test_manifest_requires_exactly_the_available_rpms(self) -> None:
        with tempfile.TemporaryDirectory(dir="/tmp") as temporary:
            packages = Path(temporary) / "packages"
            (packages / "RPMS").mkdir(parents=True)
            (packages / "SRPMS").mkdir()
            rpm = packages / "RPMS" / "example-1.0-1.x86_64.rpm"
            srpm = packages / "SRPMS" / "example-1.0-1.src.rpm"
            rpm.write_bytes(b"binary-rpm")
            srpm.write_bytes(b"source-rpm")
            lines = [
                "MUNGE_VERSION=0.5.18",
                "MUNGE_SHA256=" + "a" * 64,
                "SLURM_VERSION=26.05.5",
                "SLURM_SHA256=" + "b" * 64,
            ]
            for path in (rpm, srpm):
                relative = path.relative_to(packages)
                digest = hashlib.sha256(path.read_bytes()).hexdigest()
                lines.append(f"{digest}  {relative}")
            (packages / "BUILD-MANIFEST").write_text(
                "\n".join(lines) + "\n", encoding="ascii"
            )
            valid = run_bash(
                f"source {COMMON!s}; build_verify_manifest {packages!s}"
            )
            self.assertEqual(valid.returncode, 0, valid.stderr)

            (packages / "RPMS" / "unrecorded-1.0.rpm").write_bytes(b"extra")
            invalid = run_bash(
                f"source {COMMON!s}; build_verify_manifest {packages!s}"
            )
            self.assertNotEqual(invalid.returncode, 0)
            self.assertIn("exactly", invalid.stderr)

    def test_manifest_sources_must_match_approved_pins(self) -> None:
        with tempfile.TemporaryDirectory(dir="/tmp") as temporary:
            packages = Path(temporary) / "packages"
            (packages / "RPMS").mkdir(parents=True)
            (packages / "SRPMS").mkdir()
            (packages / "BUILD-MANIFEST").write_text(
                "MUNGE_VERSION=0.5.18\n"
                + "MUNGE_SHA256="
                + "a" * 64
                + "\nSLURM_VERSION=26.05.4\n"
                + "SLURM_SHA256="
                + "b" * 64
                + "\n",
                encoding="ascii",
            )
            result = run_bash(
                f"source {COMMON!s}; "
                "MUNGE_VERSION=0.5.18; MUNGE_SHA256="
                + "a" * 64
                + "; SLURM_VERSION=26.05.5; SLURM_SHA256="
                + "b" * 64
                + f"; build_verify_manifest_sources {packages!s}"
            )
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("SLURM_VERSION", result.stderr)

    def test_source_override_requires_version_and_digest_pair(self) -> None:
        result = subprocess.run(
            [
                str(ROOT / "scripts" / "build-rpms.sh"),
                "--slurm-version",
                "99.1.0",
            ],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("together", result.stderr)

    def test_containerfile_delegates_each_phase_to_public_script(self) -> None:
        dockerfile = (ROOT / "Containerfile").read_text(encoding="utf-8")
        for command in (
            "/opt/slurm-source/scripts/build-rpms.sh",
            "/opt/slurm-source/scripts/install-runtime.sh",
            "/opt/slurm-source/scripts/configure-runtime.sh",
        ):
            self.assertIn(command, dockerfile)
        # Slurm dependency installation remains reusable outside a container.
        # Disposable .NET build/development stages may install their SDK tools
        # directly because those packages are not part of Slurm RPM assembly.
        self.assertIn("aspnetcore-runtime-10.0", (ROOT / "scripts" / "install-runtime.sh").read_text())
        self.assertNotIn("useradd --system", dockerfile)
        self.assertNotIn("rpmbuild -", dockerfile)

    def test_container_defaults_match_versions_file(self) -> None:
        values: dict[str, str] = {}
        for line in (ROOT / "build" / "versions.conf").read_text().splitlines():
            if line and not line.startswith("#"):
                key, value = line.split("=", 1)
                values[key] = value
        dockerfile = (ROOT / "Containerfile").read_text(encoding="utf-8")
        for key in ("SLURM_VERSION", "SLURM_SHA256", "MUNGE_VERSION", "MUNGE_SHA256"):
            self.assertIn(f"ARG {key}={values[key]}", dockerfile)

    def test_configuration_phase_never_generates_secret_material(self) -> None:
        script = (ROOT / "scripts" / "configure-runtime.sh").read_text(
            encoding="utf-8"
        )
        for forbidden in ("/dev/urandom", "openssl rand", "MUNGE_KEY_FILE="):
            self.assertNotIn(forbidden, script)


if __name__ == "__main__":
    unittest.main()
