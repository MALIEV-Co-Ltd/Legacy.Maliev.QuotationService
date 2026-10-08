"""Exact source-bound two-case native evidence; uses the already reviewed c821 strict TRX reader."""
import hashlib
from pathlib import Path
from check_c821_focused_results import case_hash, validate

repository = Path(__file__).resolve().parents[1]
source = repository / "Legacy.Maliev.QuotationService.Tests/Controllers/QualificationOutcomeWireSourceTests.cs"
if hashlib.sha256(source.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != "de94c9ef71db0f7279f0a07f3e44982daf43ff04c958521d7fc2a7751067bdba":
    raise ValueError("Reviewed wire-source native inventory changed")
specification = {
    "className": "Legacy.Maliev.QuotationService.Tests.Controllers.QualificationOutcomeWireSourceTests",
    "methods": {
        "ActualControllerSerializer_EmitsReviewedSyntheticWire": {
            "parameters": [{"name": "caseName", "type": "string"}],
            "cases": [case_hash(["empty"]), case_hash(["mixed"])],
        },
    },
}
validate(repository / "TestResults/QualificationWire/qualification-wire.trx", specification, repository / "TestResults")
