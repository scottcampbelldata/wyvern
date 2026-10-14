"""Wyvern's private release key stays on Ubuntu; only public keys/signatures leave it."""
import base64
import json
import os
from pathlib import Path
import re
import sys

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, utils

KEY = Path.home() / ".local/share/wyvern-release/update-signing-key.pem"


def sign(key, manifest):
    if not 0 < len(manifest) <= 4096:
        raise ValueError("Invalid manifest size")
    data = json.loads(manifest)
    if not isinstance(data, dict):
        raise ValueError("Expected a manifest object")
    version = data.get("version", "")
    if (not isinstance(version, str)
            or not re.fullmatch(r"[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}", version)
            or data.get("file") != f"WyvernSetup-{version}.exe"
            or type(data.get("size")) is not int
            or not 0 < data["size"] <= 300 * 1024 * 1024
            or not isinstance(data.get("sha256"), str)
            or not re.fullmatch(r"[0-9a-f]{64}", data["sha256"])):
        raise ValueError("Invalid Wyvern release manifest")
    r, s = utils.decode_dss_signature(key.sign(manifest, ec.ECDSA(hashes.SHA256())))
    return r.to_bytes(32, "big") + s.to_bytes(32, "big")


def check():
    key = ec.generate_private_key(ec.SECP256R1())
    valid = {"version": "1.8.4", "file": "WyvernSetup-1.8.4.exe", "size": 123, "sha256": "a" * 64}
    manifest = json.dumps(valid).encode()
    signature = sign(key, manifest)
    assert len(signature) == 64
    der = utils.encode_dss_signature(int.from_bytes(signature[:32], "big"), int.from_bytes(signature[32:], "big"))
    key.public_key().verify(der, manifest, ec.ECDSA(hashes.SHA256()))
    for field, value in [("version", "1.2"), ("file", "../other.exe"), ("size", True), ("size", 0), ("sha256", "bad")]:
        try:
            sign(key, json.dumps({**valid, field: value}).encode())
        except ValueError:
            continue
        raise AssertionError(f"Accepted invalid {field}")
    print("Signing check passed")


if __name__ == "__main__":
    if sys.argv[1:] == ["check"]:
        check()
    elif sys.argv[1:] in (["init"], ["sign"]):
        if sys.argv[1] == "init":
            KEY.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            os.chmod(KEY.parent, 0o700)
            if not KEY.exists():
                key = ec.generate_private_key(ec.SECP256R1())
                with open(os.open(KEY, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "wb") as output:
                    output.write(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
            os.chmod(KEY, 0o600)
        key = serialization.load_pem_private_key(KEY.read_bytes(), password=None)
        if not isinstance(key, ec.EllipticCurvePrivateKey) or not isinstance(key.curve, ec.SECP256R1):
            raise ValueError("Expected a P-256 release key")
        if sys.argv[1] == "init":
            public = key.public_key().public_bytes(serialization.Encoding.X962, serialization.PublicFormat.UncompressedPoint)[1:]
            print(base64.b64encode(public).decode())
        else:
            print(base64.b64encode(sign(key, sys.stdin.buffer.read(4097))).decode())
    else:
        raise SystemExit("Usage: server-signing.py init|sign|check")
