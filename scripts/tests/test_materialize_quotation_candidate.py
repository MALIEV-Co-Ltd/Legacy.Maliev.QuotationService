import base64
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("materializer", Path(__file__).parents[1] / "materialize_quotation_candidate.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


def packet(mutate=None, scope=None):
    files = {f"scripts/source-{n}.txt": f"raw-{n}\r\n".encode() for n in range(75)}
    if scope == "admission-race":
        files = {path: b"raw\r\n" for path in (
            "Legacy.Maliev.QuotationService.Data/QuotationRepositories.cs",
            "Legacy.Maliev.QuotationService.Tests/Controllers/QuotationInvoiceCapabilityHttpTests.cs",
            "scripts/c821-focused-inventory.json",
        )}
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w") as archive:
        for path, data in files.items():
            info = zipfile.ZipInfo(path)
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            if mutate:
                mutate(info)
            archive.writestr(info, data)
    capsule = stream.getvalue()
    m = {"schemaVersion": 1, "acceptedBase": "a" * 40, "sourcePins": {"IAM": "b" * 40},
         "sourceFiles": [{"path": p, "bytes": len(b), "sha256": gate.sha256(b)} for p, b in files.items()],
         "capsuleBytes": len(capsule), "capsuleSha256": gate.sha256(capsule)}
    raw = json.dumps(m).encode()
    policy = {k: copy.deepcopy(m[k]) for k in ("acceptedBase", "sourcePins", "sourceFiles")}
    if scope is not None:
        m["qualificationScope"] = scope
        policy["qualificationScope"] = scope
        raw = json.dumps(m).encode()
    policy["manifestSha256"] = gate.sha256(raw)
    return raw, capsule, policy, files


class TransportTests(unittest.TestCase):
    def test_admission_scope_accepts_only_exact_three_paths(self):
        raw, capsule, policy, files = packet(scope="admission-race")
        _, actual = gate.validate_capsule(raw, capsule, policy)
        self.assertEqual(files, actual)
        for replacement in ("other.cs", "scripts/source-0.txt"):
            m, p = json.loads(raw), copy.deepcopy(policy)
            m["sourceFiles"][0]["path"] = replacement
            p["sourceFiles"] = copy.deepcopy(m["sourceFiles"])
            changed = json.dumps(m).encode()
            p["manifestSha256"] = gate.sha256(changed)
            with self.subTest(replacement=replacement), self.assertRaisesRegex(ValueError, "expanded inventory"):
                gate.validate_capsule(changed, capsule, p)

    def test_full_scope_cannot_consume_three_file_capsule(self):
        raw, capsule, policy, _ = packet(scope="admission-race")
        m = json.loads(raw)
        del m["qualificationScope"]
        del policy["qualificationScope"]
        raw = json.dumps(m).encode()
        policy["manifestSha256"] = gate.sha256(raw)
        with self.assertRaisesRegex(ValueError, "expanded inventory"):
            gate.validate_capsule(raw, capsule, policy)

    def test_unknown_or_mismatched_scope_is_denied(self):
        raw, capsule, policy, _ = packet(scope="unknown")
        with self.assertRaisesRegex(ValueError, "unknown qualification scope"):
            gate.validate_capsule(raw, capsule, policy)
        raw, capsule, policy, _ = packet(scope="admission-race")
        policy["qualificationScope"] = "full-candidate"
        with self.assertRaisesRegex(ValueError, "qualification scope mismatch"):
            gate.validate_capsule(raw, capsule, policy)

    def test_raw_crlf_exact_readback(self):
        raw, capsule, policy, files = packet()
        _, actual = gate.validate_capsule(raw, capsule, policy)
        self.assertEqual(files, actual)

    def test_blob_object_hash_is_recomputed(self):
        data = b"raw\r\n"
        oid = hashlib.sha1(b"blob 5\0" + data).hexdigest()
        obj = {"sha": oid, "encoding": "base64", "content": base64.b64encode(data).decode(), "size": 5}
        self.assertEqual(data, gate.decode_blob(json.dumps(obj).encode(), oid))
        obj["content"] = base64.b64encode(b"fake!").decode()
        with self.assertRaises(ValueError):
            gate.decode_blob(json.dumps(obj).encode(), oid)

    def test_blob_size_identity_and_encoding_fail_closed(self):
        for field, value in (("size", 4), ("sha", "b" * 40), ("encoding", "utf8")):
            data = b"raw\r\n"
            oid = hashlib.sha1(b"blob 5\0" + data).hexdigest()
            obj = {"sha": oid, "encoding": "base64", "content": base64.b64encode(data).decode(), "size": 5}
            obj[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                gate.decode_blob(json.dumps(obj).encode(), oid)

    def test_duplicate_json_key_denied(self):
        with self.assertRaises(ValueError):
            gate.parse_json(b'{"a":1,"a":2}')

    def test_path_escapes_and_git_metadata_denied(self):
        for path in ("../a", "/a", "a//b", "a/./b", "a\\b", "C:a", ".git/config", "a/../b"):
            with self.subTest(path=path), self.assertRaises(ValueError):
                gate.canonical_path(path)

    def test_manifest_and_capsule_tampering_denied(self):
        raw, capsule, policy, _ = packet()
        for m, z in ((raw + b" ", capsule), (raw, capsule[:-1] + b"x")):
            with self.assertRaises(ValueError):
                gate.validate_capsule(m, z, policy)

    def test_reviewed_inventory_and_pins_bound(self):
        raw, capsule, policy, _ = packet()
        for field in ("acceptedBase", "sourcePins", "sourceFiles"):
            changed = copy.deepcopy(policy)
            changed[field] = None
            with self.subTest(field=field), self.assertRaises(ValueError):
                gate.validate_capsule(raw, capsule, changed)

    def test_symlink_and_directory_entries_denied(self):
        for mode in (0o120777, 0o040755):
            def change(info):
                info.external_attr = mode << 16
            raw, capsule, policy, _ = packet(change)
            with self.assertRaises(ValueError):
                gate.validate_capsule(raw, capsule, policy)

    def test_unexpected_duplicate_and_noncanonical_entries_denied(self):
        for name in ("unexpected.txt", "../outside", "scripts/source-1.txt"):
            def change(info):
                if info.filename == "scripts/source-0.txt":
                    info.filename = name
            raw, capsule, policy, _ = packet(change)
            with self.subTest(name=name), self.assertRaises(ValueError):
                gate.validate_capsule(raw, capsule, policy)

    def test_materialization_requires_exact_clean_git_base(self):
        _, _, policy, files = packet()
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            def git(*args):
                return subprocess.check_output(["git", "-C", tmp, *args], stderr=subprocess.STDOUT).decode().strip()
            git("init", "-q")
            git("-c", "user.name=Transport Test", "-c", "user.email=transport@example.invalid", "commit", "-q", "--allow-empty", "-m", "temporary test base")
            with self.assertRaises(ValueError):
                gate.materialize(root, policy, files)
            policy["acceptedBase"] = git("rev-parse", "HEAD")
            (root / "foreign.txt").write_text("preserve")
            with self.assertRaises(ValueError):
                gate.materialize(root, policy, files)
            (root / "foreign.txt").unlink()
            gate.materialize(root, policy, files)
            gate.verify_source(root, policy)
            (root / "scripts/source-0.txt").write_bytes(b"changed")
            with self.assertRaises(ValueError):
                gate.verify_source(root, policy)


if __name__ == "__main__":
    unittest.main()
