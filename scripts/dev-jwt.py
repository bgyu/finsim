#!/usr/bin/env python3
"""Generate local-only RS256 JWKS and JWT files without printing secrets."""

from __future__ import annotations

import argparse
import base64
import json
import os
from pathlib import Path
import subprocess
import time
import uuid


def b64url(value: bytes) -> str:
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode("ascii")


def read_tlv(data: bytes, offset: int) -> tuple[int, bytes, int]:
    if offset + 2 > len(data):
        raise ValueError("truncated DER")
    tag = data[offset]
    length = data[offset + 1]
    offset += 2
    if length & 0x80:
        width = length & 0x7F
        if width == 0 or width > 4 or offset + width > len(data):
            raise ValueError("invalid DER length")
        length = int.from_bytes(data[offset : offset + width], "big")
        offset += width
    end = offset + length
    if end > len(data):
        raise ValueError("truncated DER value")
    return tag, data[offset:end], end


def rsa_public_numbers(private_key: Path) -> tuple[bytes, bytes]:
    result = subprocess.run(
        ["openssl", "pkey", "-in", str(private_key), "-pubout", "-outform", "DER"],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
    )
    tag, spki, end = read_tlv(result.stdout, 0)
    if tag != 0x30 or end != len(result.stdout):
        raise ValueError("invalid SubjectPublicKeyInfo")
    tag, _, offset = read_tlv(spki, 0)
    if tag != 0x30:
        raise ValueError("invalid public-key algorithm")
    tag, bit_string, offset = read_tlv(spki, offset)
    if tag != 0x03 or offset != len(spki) or not bit_string or bit_string[0] != 0:
        raise ValueError("invalid RSA public-key bit string")
    tag, rsa_sequence, end = read_tlv(bit_string[1:], 0)
    if tag != 0x30 or end != len(bit_string) - 1:
        raise ValueError("invalid RSA public key")
    tag, modulus, offset = read_tlv(rsa_sequence, 0)
    if tag != 0x02:
        raise ValueError("invalid RSA modulus")
    tag, exponent, offset = read_tlv(rsa_sequence, offset)
    if tag != 0x02 or offset != len(rsa_sequence):
        raise ValueError("invalid RSA exponent")
    return modulus.lstrip(b"\x00"), exponent.lstrip(b"\x00")


def atomic_write(path: Path, content: bytes, mode: int) -> None:
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    temporary = path.parent / f".{path.name}.{uuid.uuid4().hex}.tmp"
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, mode)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        os.chmod(path, mode)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def write_jwks(args: argparse.Namespace) -> None:
    modulus, exponent = rsa_public_numbers(args.private_key)
    document = {
        "keys": [
            {
                "kty": "RSA",
                "use": "sig",
                "alg": "RS256",
                "kid": args.key_id,
                "n": b64url(modulus),
                "e": b64url(exponent),
            }
        ]
    }
    atomic_write(
        args.output,
        json.dumps(document, sort_keys=True, separators=(",", ":")).encode("ascii") + b"\n",
        0o644,
    )


def write_token(args: argparse.Namespace) -> None:
    now = int(time.time())
    header = {"alg": "RS256", "kid": args.key_id, "typ": "JWT"}
    payload = {
        "iss": args.issuer,
        "aud": args.audience,
        "sub": args.subject,
        "scope": args.scope,
        "iat": now,
        "nbf": now - 5,
        "exp": now + args.lifetime,
        "jti": str(uuid.uuid4()),
    }
    encoded_header = b64url(
        json.dumps(header, sort_keys=True, separators=(",", ":")).encode("ascii")
    )
    encoded_payload = b64url(
        json.dumps(payload, sort_keys=True, separators=(",", ":")).encode("ascii")
    )
    signing_input = f"{encoded_header}.{encoded_payload}".encode("ascii")
    signature = subprocess.run(
        ["openssl", "dgst", "-sha256", "-sign", str(args.private_key)],
        input=signing_input,
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
    ).stdout
    atomic_write(
        args.output,
        signing_input + b"." + b64url(signature).encode("ascii") + b"\n",
        0o600,
    )


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser()
    subcommands = result.add_subparsers(required=True)
    jwks = subcommands.add_parser("jwks")
    jwks.add_argument("--private-key", type=Path, required=True)
    jwks.add_argument("--key-id", required=True)
    jwks.add_argument("--output", type=Path, required=True)
    jwks.set_defaults(action=write_jwks)
    token = subcommands.add_parser("token")
    token.add_argument("--private-key", type=Path, required=True)
    token.add_argument("--key-id", required=True)
    token.add_argument("--issuer", required=True)
    token.add_argument("--audience", required=True)
    token.add_argument("--subject", required=True)
    token.add_argument("--scope", required=True)
    token.add_argument("--lifetime", type=int, default=86400)
    token.add_argument("--output", type=Path, required=True)
    token.set_defaults(action=write_token)
    return result


def main() -> int:
    args = parser().parse_args()
    if not args.private_key.is_file() or args.private_key.is_symlink():
        raise SystemExit("private key must be a regular non-symlink file")
    if hasattr(args, "lifetime") and not 60 <= args.lifetime <= 86400:
        raise SystemExit("token lifetime must be between 60 and 86400 seconds")
    args.action(args)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
