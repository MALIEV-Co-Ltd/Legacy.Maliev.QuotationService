"""Materialize only the reviewed raw source capsule; never commit candidate C#."""

import argparse
import base64
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import urllib.request
import zipfile

REPOSITORY = "MALIEV-Co-Ltd/Legacy.Maliev.QuotationService"
MAX_API_BYTES = 16 * 1024 * 1024
MAX_CAPSULE_BYTES = 8 * 1024 * 1024
MAX_FILE_BYTES = 2 * 1024 * 1024
MAX_EXPANDED_BYTES = 16 * 1024 * 1024


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key] = value
    return result


def parse_json(data):
    return json.loads(data, object_pairs_hook=unique_object)


def canonical_path(value):
    if not isinstance(value, str) or not value or "\\" in value or ":" in value:
        raise ValueError("invalid source path")
    parts = value.split("/")
    if any(p in ("", ".", "..", ".git") for p in parts):
        raise ValueError("noncanonical source path")
    if str(PurePosixPath(value)) != value:
        raise ValueError("noncanonical source path")
    return value


def decode_blob(response, expected_oid):
    if not re.fullmatch(r"[0-9a-f]{40}", expected_oid):
        raise ValueError("invalid blob identity")
    if len(response) > MAX_API_BYTES:
        raise ValueError("Git API response too large")
    obj = parse_json(response)
    if obj.get("sha") != expected_oid or obj.get("encoding") != "base64":
        raise ValueError("Git API blob identity/encoding mismatch")
    content = obj.get("content")
    if not isinstance(content, str):
        raise ValueError("missing Git blob content")
    data = base64.b64decode(content.replace("\n", ""), validate=True)
    if len(data) > MAX_CAPSULE_BYTES or obj.get("size") != len(data):
        raise ValueError("Git blob size mismatch")
    oid = hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
    if oid != expected_oid:
        raise ValueError("Git object hash mismatch")
    return data


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError("Git API redirects prohibited")


def fetch_blob(oid):
    if not re.fullmatch(r"[0-9a-f]{40}", oid):
        raise ValueError("invalid blob identity")
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "quotation-source-qualification"}
    token = os.environ.get("GH_TOKEN")
    if token:
        headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(f"https://api.github.com/repos/{REPOSITORY}/git/blobs/{oid}", headers=headers)
    with urllib.request.build_opener(NoRedirect()).open(req, timeout=30) as response:
        data = response.read(MAX_API_BYTES + 1)
    return decode_blob(data, oid)


def validate_capsule(manifest_bytes, capsule_bytes, policy):
    if sha256(manifest_bytes) != policy["manifestSha256"]:
        raise ValueError("reviewed manifest digest mismatch")
    m = parse_json(manifest_bytes)
    if m["schemaVersion"] != 1 or m["acceptedBase"] != policy["acceptedBase"]:
        raise ValueError("candidate base mismatch")
    if m["sourcePins"] != policy["sourcePins"]:
        raise ValueError("dependency pins mismatch")
    if m["sourceFiles"] != policy["sourceFiles"]:
        raise ValueError("reviewed file inventory mismatch")
    if len(capsule_bytes) > MAX_CAPSULE_BYTES or len(capsule_bytes) != m["capsuleBytes"]:
        raise ValueError("capsule size mismatch")
    if sha256(capsule_bytes) != m["capsuleSha256"]:
        raise ValueError("capsule digest mismatch")
    expected = {}
    for row in m["sourceFiles"]:
        path = canonical_path(row["path"])
        if path in expected or not re.fullmatch(r"[0-9a-f]{64}", row["sha256"]):
            raise ValueError("invalid source inventory")
        if type(row["bytes"]) is not int or not 0 < row["bytes"] <= MAX_FILE_BYTES:
            raise ValueError("invalid file size")
        expected[path] = row
    if len(expected) != 74 or sum(row["bytes"] for row in expected.values()) > MAX_EXPANDED_BYTES:
        raise ValueError("invalid expanded inventory")
    files = {}
    with zipfile.ZipFile(io.BytesIO(capsule_bytes)) as archive:
        infos = archive.infolist()
        if len(infos) != len(expected):
            raise ValueError("archive entry count mismatch")
        for info in infos:
            path = canonical_path(info.filename)
            if path not in expected or path in files:
                raise ValueError("unexpected or duplicate archive entry")
            if info.create_system != 3 or stat.S_IFMT(info.external_attr >> 16) != stat.S_IFREG:
                raise ValueError("archive entry is not a regular file")
            if info.flag_bits & 1 or info.file_size != expected[path]["bytes"]:
                raise ValueError("archive file size/encoding mismatch")
            with archive.open(info) as source:
                data = source.read(MAX_FILE_BYTES + 1)
            if len(data) != expected[path]["bytes"] or sha256(data) != expected[path]["sha256"]:
                raise ValueError("raw file digest mismatch")
            files[path] = data
    return m, files


def verify_source(root, policy):
    head = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    if head != policy["acceptedBase"]:
        raise ValueError("candidate base changed")
    changed = subprocess.check_output(["git", "-C", str(root), "diff", "HEAD", "--name-only", "-z"]).decode().split("\0")
    allowed = {row["path"] for row in policy["sourceFiles"]}
    if set(filter(None, changed)) - allowed:
        raise ValueError("tracked base source changed outside reviewed candidate")
    for row in policy["sourceFiles"]:
        target = root / canonical_path(row["path"])
        for parent in (target, *target.parents):
            if parent == root.parent:
                break
            if parent.is_symlink():
                raise ValueError("symlink in candidate source path")
        data = target.read_bytes()
        if len(data) != row["bytes"] or sha256(data) != row["sha256"]:
            raise ValueError("materialized source changed: " + row["path"])


def materialize(root, policy, files):
    head = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    if head != policy["acceptedBase"]:
        raise ValueError("checkout base mismatch")
    if subprocess.check_output(["git", "-C", str(root), "status", "--porcelain", "--untracked-files=all"]):
        raise ValueError("candidate checkout must be clean before materialization")
    # Check every destination before writing any source bytes.
    for path in files:
        target = root / path
        for parent in (target, *target.parents):
            if parent == root.parent:
                break
            if parent.is_symlink():
                raise ValueError("symlink in destination")
        if target.exists() and not target.is_file():
            raise ValueError("destination is not a file")
    for path, data in files.items():
        target = root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    verify_source(root, policy)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--policy", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--manifest-blob")
    parser.add_argument("--capsule-blob")
    parser.add_argument("--verify-only", action="store_true")
    parser.add_argument("--receipt", type=Path)
    args = parser.parse_args()
    policy = parse_json(args.policy.read_bytes())
    root = args.candidate.resolve(strict=True)
    transport_commit = os.environ.get("GITHUB_SHA")
    if transport_commit:
        actual = subprocess.check_output(["git", "-C", str(args.policy.resolve().parents[1]), "rev-parse", "HEAD"], text=True).strip()
        if not re.fullmatch(r"[0-9a-f]{40}", transport_commit) or actual != transport_commit:
            raise ValueError("transport checkout differs from workflow commit")
    if args.verify_only:
        verify_source(root, policy)
        print(f"Reviewed raw candidate source remains unchanged ({len(policy["sourceFiles"])} files).")
        return
    if args.receipt is None or args.receipt.exists():
        raise ValueError("fresh evidence receipt path required")
    manifest_bytes = fetch_blob(args.manifest_blob or "")
    capsule_bytes = fetch_blob(args.capsule_blob or "")
    _, files = validate_capsule(manifest_bytes, capsule_bytes, policy)
    materialize(root, policy, files)
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    args.receipt.write_text(json.dumps({"manifestSha256": policy["manifestSha256"], "manifestBlob": args.manifest_blob,
                                      "capsuleBlob": args.capsule_blob, "acceptedBase": policy["acceptedBase"],
                                      "sourcePins": policy["sourcePins"], "sourceFiles": policy["sourceFiles"],
                                      "transportCommit": transport_commit, "nativeValidated": False}, indent=2) + "\n")
    print(f"Reviewed raw candidate materialized ({len(policy["sourceFiles"])} files); native validation pending.")


if __name__ == "__main__":
    main()
