"""Tests for reusable package build/install/configuration interfaces."""

from __future__ import annotations

import hashlib
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
