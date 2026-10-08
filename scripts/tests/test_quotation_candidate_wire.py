import copy
import importlib.util
import io
import os
import time
from types import SimpleNamespace
import json
from pathlib import Path
import re
import stat
import sys
import tempfile
import unittest
from unittest.mock import patch
import uuid
import xml.etree.ElementTree as ET
import zipfile

SCRIPTS = Path(__file__).parents[1]
sys.path.insert(0, str(SCRIPTS))
spec = importlib.util.spec_from_file_location("candidate_wire", SCRIPTS / "check_quotation_candidate_wire.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)
NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value), encoding="utf-8")


class CandidateWireTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve(); self.candidate = self.root / "candidate"
        self.policy_path = self.root / "scripts/quotation-admission-race-policy.json"
        self.materialization_path = self.root / "evidence/source-materialization.json"
        self.head, self.base = "1" * 40, "2" * 40
        self.env = {"GITHUB_WORKSPACE": str(self.root), "GITHUB_SHA": self.head, "GITHUB_RUN_ID": "12345",
                    "GITHUB_RUN_ATTEMPT": "2", "GITHUB_REPOSITORY": gate.transport.REPOSITORY}
        self.inherited = {}
        for path in gate.SOURCE_PATHS:
            data = ("original source " + path).encode(); target = self.candidate / path
            target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(data); self.inherited[path] = data
        files = {"Legacy.Maliev.QuotationService.Data/QuotationRepositories.cs": b"reviewed repository",
                 "Legacy.Maliev.QuotationService.Tests/Controllers/QuotationInvoiceCapabilityHttpTests.cs": b"reviewed race cases",
                 "scripts/c821-focused-inventory.json": b"reviewed inventory", gate.TEST_SOURCE: b"reviewed opt-in test"}
        for path, data in files.items():
            target = self.candidate / path; target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(data)
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            for path, data in files.items():
                info = zipfile.ZipInfo(path); info.create_system = 3; info.external_attr = (stat.S_IFREG | 0o644) << 16
                archive.writestr(info, data)
        self.capsule = buffer.getvalue()
        rows = [{"path": path, "sha256": gate.digest(data), "bytes": len(data)} for path, data in files.items()]
        self.policy = {"qualificationScope": "admission-race-wire", "acceptedBase": self.base,
                       "sourcePins": {"retainedDefaults": "3" * 40}, "sourceFiles": rows}
        self.manifest = json.dumps({"schemaVersion": 1, **self.policy, "capsuleSha256": gate.digest(self.capsule),
                                   "capsuleBytes": len(self.capsule)}).encode()
        self.policy["manifestSha256"] = gate.digest(self.manifest); write_json(self.policy_path, self.policy)
        self.materialization = {"manifestSha256": self.policy["manifestSha256"], "manifestBlob": "4" * 40, "capsuleBlob": "5" * 40,
                                "acceptedBase": self.base, "sourcePins": self.policy["sourcePins"], "sourceFiles": rows,
                                "transportCommit": self.head, "nativeValidated": False}
        write_json(self.materialization_path, self.materialization)
        self.trusted = {"scripts/quotation-admission-race-policy.json": self.policy_path.read_bytes()}
        for name in ("check_quotation_candidate_wire.py", "materialize_quotation_candidate.py", "check_c821_focused_results.py", "run_quotation_candidate_native.sh"):
            path = self.root / "scripts" / name; data = (SCRIPTS / name).read_bytes(); path.write_bytes(data)
            self.trusted["scripts/" + name] = data
        for name in ("quotation-admission-race-qualification.yml", "quotation-fixture-corrected-qualification.yml"):
            relative = ".github/workflows/" + name; target = self.root / relative
            target.parent.mkdir(parents=True, exist_ok=True); data = (SCRIPTS.parent / relative).read_bytes()
            target.write_bytes(data); self.trusted[relative] = data
        self.output = self.candidate / "TestResults/QualificationWire"; self.output.mkdir(parents=True)
        expected = re.findall(r'"""(.*?)"""', (SCRIPTS.parent / gate.TEST_SOURCE).read_text(encoding="utf-8"), re.S)
        self.assertEqual(2, len(expected)); self.assemblies = []
        for name, project in gate.ASSEMBLIES.items():
            data = ("actual binary " + name).encode(); self.assemblies.append({"name": name, "sha256": gate.digest(data)})
            for path in {gate.TEST_ASSEMBLY + "/bin/Release/net10.0/" + name + ".dll", project + "/bin/Release/net10.0/" + name + ".dll"}:
                target = self.candidate / path; target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(data)
        for name, text in zip(("empty", "mixed"), expected):
            data = text.encode(); self.assertEqual(gate.WIRE_FINGERPRINTS[name], (len(data), gate.digest(data)))
            (self.output / (name + ".json")).write_bytes(data)
            write_json(self.output / (name + ".metadata.json"), {"caseName": name, "statusCode": 200,
                "contentType": "application/json; charset=utf-8", "camelCase": True, "ignoreCondition": "WhenWritingNull",
                "actualDtoType": "Legacy.Maliev.QuotationService.Application.Models.QualificationOutcomeReadback",
                "actualMvcExecutorType": "Microsoft.AspNetCore.Mvc.Infrastructure.SystemTextJsonResultExecutor", "assemblies": self.assemblies})
        report = ET.Element(NS + "TestRun"); definitions = ET.SubElement(report, NS + "TestDefinitions")
        results = ET.SubElement(report, NS + "Results"); entries = ET.SubElement(report, NS + "TestEntries")
        for name in ("empty", "mixed"):
            identity, execution = str(uuid.uuid4()), str(uuid.uuid4())
            case = ET.SubElement(definitions, NS + "UnitTest", id=identity, name=gate.CLASS + "." + gate.METHOD)
            ET.SubElement(case, NS + "Execution", id=execution)
            ET.SubElement(case, NS + "TestMethod", name=gate.METHOD, className=gate.CLASS,
                          codeBase=str(self.candidate / gate.TEST_ASSEMBLY / "bin/Release/net10.0" / (gate.TEST_ASSEMBLY + ".dll")), adapterTypeName="executor://xunit/VsTestRunner3/netcore/")
            ET.SubElement(results, NS + "UnitTestResult", testId=identity, executionId=execution,
                          testName=gate.CLASS + "." + gate.METHOD + '(caseName: "' + name + '")', outcome="Passed")
            ET.SubElement(entries, NS + "TestEntry", testId=identity, executionId=execution, testListId=str(uuid.uuid4()))
        summary = ET.SubElement(report, NS + "ResultSummary", outcome="Completed")
        from check_c821_focused_results import COUNTERS
        ET.SubElement(summary, NS + "Counters", **{key: "2" if key in ("total", "executed", "passed") else "0" for key in COUNTERS})
        self.trx = self.output / "qualification-wire.trx"; self.trx.write_bytes(ET.tostring(report)); self.verifications = 0

    def fake_git(self, root, *args):
        if args == ("rev-parse", "HEAD"): return self.head.encode()
        if args == ("rev-parse", self.base + "^{tree}"): return ("6" * 40).encode()
        if args[0] == "show":
            revision, path = args[1].split(":", 1)
            if root == self.candidate:
                self.assertEqual(self.base, revision); return self.inherited[path]
            self.assertEqual(self.head, revision); return self.trusted[path]
        raise AssertionError(args)

    def fake_verify_source(self, root, policy):
        self.verifications += 1
        for row in policy["sourceFiles"]:
            data = (root / row["path"]).read_bytes()
            if (len(data), gate.digest(data)) != (row["bytes"], row["sha256"]): raise ValueError("materialized source changed")

    def verify(self, **kwargs):
        with patch.object(gate, "git", side_effect=self.fake_git), patch.object(gate.transport, "verify_source", side_effect=self.fake_verify_source), \
                patch.object(gate.transport, "fetch_blob", side_effect=lambda oid: self.manifest if oid == "4" * 40 else self.capsule):
            return gate.verify(kwargs.get("candidate", self.candidate), kwargs.get("policy", self.policy_path),
                               kwargs.get("materialization", self.materialization_path), kwargs.get("env", self.env))

    def test_exact_raw_candidate_joins_original_sources_binaries_cases_and_run(self):
        path, receipt = self.verify(); self.assertEqual(self.output / gate.RECEIPT_NAME, path)
        self.assertEqual(2, self.verifications); self.assertEqual("raw-candidate", receipt["mode"])
        self.assertNotIn("head", receipt); self.assertNotIn("tree", receipt)
        self.assertEqual(self.base, receipt["acceptedBase"]); self.assertEqual(gate.digest(self.capsule), receipt["capsuleSha256"])
        self.assertEqual(7, len(receipt["sources"])); self.assertEqual(4, len(receipt["assemblies"]))
        self.assertEqual(6, len(receipt["trustedSources"])); self.assertFalse(path.exists())

    def test_context_materialization_and_path_mutations_reject(self):
        for key, value in (("GITHUB_SHA", "9" * 40), ("GITHUB_RUN_ID", "0"), ("GITHUB_RUN_ATTEMPT", "0"),
                           ("GITHUB_REPOSITORY", "other/repo"), ("GITHUB_WORKSPACE", str(self.candidate))):
            env = dict(self.env); env[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError): self.verify(env=env)
        for key, value in (("nativeValidated", True), ("transportCommit", "9" * 40), ("manifestSha256", "0" * 64),
                           ("acceptedBase", "9" * 40), ("sourcePins", {}), ("sourceFiles", []), ("unknown", True)):
            write_json(self.materialization_path, {**self.materialization, key: value})
            with self.subTest(receiptKey=key), self.assertRaises(ValueError): self.verify()
        write_json(self.materialization_path, self.materialization)
        with self.assertRaises(ValueError): self.verify(candidate=self.root)
        with self.assertRaises(ValueError): self.verify(materialization=self.root / "evidence/other.json")
        with self.assertRaises(ValueError): self.verify(policy=self.root / "scripts/other.json")

    def test_capsule_manifest_and_raw_source_mutations_reject(self):
        manifest, capsule = self.manifest, self.capsule; self.manifest += b" "
        with self.assertRaises(ValueError): self.verify()
        self.manifest = manifest; self.capsule += b"x"
        with self.assertRaises(ValueError): self.verify()
        self.capsule = capsule; (self.candidate / gate.TEST_SOURCE).write_bytes(b"substituted test")
        with self.assertRaises(ValueError): self.verify()

    def test_inherited_source_release_binaries_and_trusted_helpers_reject_substitution(self):
        paths = [self.candidate / gate.SOURCE_PATHS[0],
                 self.candidate / (gate.TEST_ASSEMBLY + "/bin/Release/net10.0/Legacy.Maliev.QuotationService.Api.dll"),
                 self.root / "scripts/check_quotation_candidate_wire.py", self.root / "scripts/materialize_quotation_candidate.py",
                 self.root / "scripts/check_c821_focused_results.py", self.root / "scripts/run_quotation_candidate_native.sh",
                 self.root / ".github/workflows/quotation-admission-race-qualification.yml"]
        for path in paths:
            original = path.read_bytes(); path.write_bytes(b"substitution")
            with self.subTest(path=path.name), self.assertRaises(ValueError): self.verify()
            path.write_bytes(original)

    def test_substituted_wire_and_metadata_schema_duplicates_missing_extra_and_oversized_reject(self):
        path = self.output / "empty.json"; original = path.read_bytes(); path.write_bytes(b'{"requests":[]}')
        with self.assertRaises(ValueError): self.verify()
        path.write_bytes(original); path = self.output / "empty.metadata.json"; original = path.read_bytes(); metadata = json.loads(original)
        for key, value in (("caseName", "mixed"), ("statusCode", True), ("contentType", "text/plain"), ("camelCase", False),
                           ("ignoreCondition", "Never"), ("actualDtoType", "Other"), ("actualMvcExecutorType", "Other"), ("assemblies", []), ("extra", 1)):
            write_json(path, {**metadata, key: value})
            with self.subTest(key=key), self.assertRaises(ValueError): self.verify()
        for data in (b'{"caseName":"empty","caseName":"mixed"}', b"x" * (gate.MAX_JSON + 1)):
            path.write_bytes(data)
            with self.assertRaises(ValueError): self.verify()
        path.write_bytes(original); extra = self.output / "unexpected.json"; extra.write_bytes(b"{}")
        with self.assertRaises(ValueError): self.verify()
        extra.unlink(); path.unlink()
        with self.assertRaises(ValueError): self.verify()

    def test_case_loaded_assembly_hashes_must_agree(self):
        path = self.output / "mixed.metadata.json"; metadata = json.loads(path.read_text())
        metadata["assemblies"][0]["sha256"] = "0" * 64; write_json(path, metadata)
        with self.assertRaises(ValueError): self.verify()

    def test_original_trx_failed_unknown_or_unbound_cases_and_execution_joins_reject(self):
        original = self.trx.read_bytes()
        for change in ("failed", "unknown", "assembly", "foreign-same-assembly", "adapter", "duplicate-execution", "entry", "incomplete", "dtd"):
            root = ET.fromstring(original); result = root.find(NS + "Results")[0]
            method = root.find(NS + "TestDefinitions")[0].find(NS + "TestMethod")
            if change == "failed": result.set("outcome", "Failed")
            elif change == "unknown": result.set("testName", gate.CLASS + "." + gate.METHOD + '(caseName: "unknown")')
            elif change == "assembly": method.set("codeBase", "/native/Other.dll")
            elif change == "foreign-same-assembly": method.set("codeBase", str(self.root / "foreign" / (gate.TEST_ASSEMBLY + ".dll")))
            elif change == "adapter": method.set("adapterTypeName", "foreign")
            elif change == "duplicate-execution": root.find(NS + "Results")[1].set("executionId", result.get("executionId"))
            elif change == "entry": root.find(NS + "TestEntries")[0].set("testId", str(uuid.uuid4()))
            elif change == "incomplete": root.find(NS + "Results").remove(result)
            data = ET.tostring(root); self.trx.write_bytes(b"<!DOCTYPE unsafe>" + data if change == "dtd" else data)
            with self.subTest(change=change), self.assertRaises(ValueError): self.verify()
        self.trx.write_bytes(original)

    def test_stale_receipt_and_symlink_outputs_reject(self):
        receipt = self.output / gate.RECEIPT_NAME; receipt.write_bytes(b"{}")
        with self.assertRaises(ValueError): self.verify()
        receipt.unlink(); path = self.output / "empty.json"; path.unlink()
        try: path.symlink_to(self.output / "mixed.json")
        except OSError as error: self.skipTest("platform symlink privilege unavailable: " + str(error))
        with self.assertRaises(ValueError): self.verify()

    def test_duplicate_policy_receipt_keys_unbounded_receipts_and_protected_policy_mutation_reject(self):
        original = self.policy_path.read_bytes()
        self.policy_path.write_bytes(b'{"acceptedBase":"a","acceptedBase":"b"}')
        with self.assertRaises(ValueError): self.verify()
        self.policy_path.write_bytes(original)
        receipt_bytes = self.materialization_path.read_bytes()
        for data in (b'{"nativeValidated":false,"nativeValidated":true}', b"x" * (gate.MAX_JSON + 1)):
            self.materialization_path.write_bytes(data)
            with self.assertRaises(ValueError): self.verify()
        self.materialization_path.write_bytes(receipt_bytes)
        # Semantically identical policy bytes still must equal the protected Git blob.
        self.policy_path.write_bytes(original + b" ")
        with self.assertRaises(ValueError): self.verify()

    def test_second_protected_policy_path_uses_the_same_explicit_boundary(self):
        second = self.root / "scripts/quotation-fixture-corrected-policy.json"
        second.write_bytes(self.policy_path.read_bytes())
        self.trusted["scripts/quotation-fixture-corrected-policy.json"] = second.read_bytes()
        _, receipt = self.verify(policy=second)
        self.assertEqual("admission-race-wire", receipt["qualificationScope"])
        self.assertIn("scripts/quotation-fixture-corrected-policy.json", [row["path"] for row in receipt["trustedSources"]])

    def test_captured_bad_trx_cannot_be_validated_by_a_transient_good_path(self):
        original = self.trx.read_bytes()
        report = ET.fromstring(original); report.find(NS + "Results")[0].set("outcome", "Failed")
        bad = ET.tostring(report)
        reader = gate.read_file
        # Path remains a good TRX; the captured buffer is bad. Only that captured buffer may be validated/hashed.
        def captured(path, root, limit=gate.MAX_JSON):
            return bad if Path(path) == self.trx else reader(path, root, limit)
        with patch.object(gate, "read_file", side_effect=captured), self.assertRaises(ValueError): self.verify()
        self.assertEqual(original, self.trx.read_bytes())

    def test_opened_descriptor_regular_type_is_required_before_reading(self):
        path = self.output / "empty.json"; original_open, original_fstat = os.open, os.fstat; leaf = set()
        def opened(name, flags, *args, **kwargs):
            descriptor = original_open(name, flags, *args, **kwargs)
            if str(name) in (path.name, str(path)):
                if hasattr(os, "O_NONBLOCK"): self.assertTrue(flags & os.O_NONBLOCK)
                if hasattr(os, "O_NOFOLLOW"): self.assertTrue(flags & os.O_NOFOLLOW)
                leaf.add(descriptor)
            return descriptor
        def observed(descriptor):
            value = original_fstat(descriptor)
            return SimpleNamespace(st_mode=stat.S_IFIFO, st_size=0) if descriptor in leaf else value
        with patch.object(gate.os, "open", side_effect=opened), patch.object(gate.os, "fstat", side_effect=observed), \
                patch.object(gate.os, "read", side_effect=AssertionError("nonregular descriptor must not be read")):
            with self.assertRaises(ValueError): gate.read_file(path, self.candidate)
        self.assertTrue(leaf)
        for descriptor in leaf:
            with self.assertRaises(OSError): original_fstat(descriptor)

    @unittest.skipUnless(sys.platform == "linux" and hasattr(os, "mkfifo"), "real FIFO control requires Linux")
    def test_actual_fifo_without_writer_rejects_without_blocking(self):
        path = self.output / "fifo"; os.mkfifo(path)
        before = time.monotonic()
        with self.assertRaises(ValueError): gate.read_file(path, self.candidate)
        self.assertLess(time.monotonic() - before, 1)
        path.unlink()

    def test_leaf_inode_replacement_after_open_is_rejected(self):
        path = self.output / "empty.json"; original_read = os.read; changed = False
        def read(descriptor, size):
            nonlocal changed
            data = original_read(descriptor, size)
            if not changed:
                changed = True
                path.rename(path.with_suffix(".previous")); path.write_bytes(data)
            return data
        with patch.object(gate.os, "read", side_effect=read), self.assertRaises(ValueError): gate.read_file(path, self.candidate)
        self.assertTrue(changed)

    def test_ancestor_replacement_after_open_is_rejected(self):
        path = self.output / "empty.json"; original_read = os.read; changed = False
        def read(descriptor, size):
            nonlocal changed
            data = original_read(descriptor, size)
            if not changed:
                changed = True
                self.output.rename(self.output.with_name("previous-output")); self.output.mkdir()
                path.write_bytes(data)
            return data
        with patch.object(gate.os, "read", side_effect=read), self.assertRaises(ValueError): gate.read_file(path, self.candidate)
        self.assertTrue(changed)

    def test_receipt_creation_is_exclusive_and_rejects_linked_or_replaced_parent(self):
        path = self.output / gate.RECEIPT_NAME
        gate.create_receipt(path, b"reviewed receipt")
        self.assertEqual(b"reviewed receipt", path.read_bytes())
        with self.assertRaises(ValueError): gate.create_receipt(path, b"replacement")
        path.unlink()
        original_write = os.write; changed = False
        def write(descriptor, data):
            nonlocal changed
            count = original_write(descriptor, data)
            if not changed:
                changed = True
                self.output.rename(self.output.with_name("previous-output")); self.output.mkdir()
            return count
        with patch.object(gate.os, "write", side_effect=write), self.assertRaises(ValueError): gate.create_receipt(path, b"receipt")
        self.assertTrue(changed)

    def test_post_verification_detects_midflight_output_mutation(self):
        original = self.fake_verify_source
        def mutate(root, policy):
            if self.verifications: (self.output / "empty.json").write_bytes(b"{}")
            original(root, policy)
        self.fake_verify_source = mutate
        with self.assertRaises(ValueError): self.verify()


if __name__ == "__main__":
    unittest.main()
