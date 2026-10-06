"""Exact source-bound two-case native evidence; uses the already reviewed c821 strict TRX reader."""
import hashlib
from pathlib import Path
from check_c821_focused_results import case_hash, validate

repository = Path(__file__).resolve().parents[1]
source = repository / "Legacy.Maliev.QuotationService.Tests/Controllers/QualificationOutcomeWireSourceTests.cs"
if hashlib.sha256(source.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != "99f0a8361061dc23dcca38513a05e27eb0021688f1617b866e04a7b0ce4817fd":
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
