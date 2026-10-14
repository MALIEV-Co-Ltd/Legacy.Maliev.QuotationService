"""Enroll the reviewed 23 inspection cases using the existing strict scalar/TRX checker."""
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
from check_c821_focused_results import MAX_BYTES, validate_bytes

def load_inventory(repository):
    specification = json.loads((repository / "scripts/docker_context_inspection_focus.json").read_bytes())
    if sum(len(row["cases"]) for row in specification["methods"].values()) != 23:
        raise ValueError("Expected frozen 23 inspection cases")
    for name, expected in specification["sources"].items():
        actual = hashlib.sha256((repository / name).read_bytes().replace(b"\r\n", b"\n")).hexdigest()
        if actual != expected: raise ValueError("Reviewed inspection source changed")
    return specification

def validate_inspection_bytes(data, specification):
    validate_bytes(data, specification, "docker-context-inspection.trx")
    root = ET.fromstring(data.decode("utf-8-sig"))
    ns = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
    if root.findall(".//" + ns + "ErrorInfo"):
        raise ValueError("Focused evidence contains errors")
    for method in root.findall(".//" + ns + "TestMethod"):
        if Path(method.get("codeBase", "").replace("\\", "/")).name != "Legacy.Maliev.QuotationService.Tests.dll":
            raise ValueError("Wrong inspection test assembly")

def main():
    if sys.platform != "linux": raise ValueError("Actual POSIX inspection requires hosted Linux")
    repository = Path(__file__).resolve().parents[1]
    specification = load_inventory(repository)
    path = repository / "TestResults/DockerContextInspection/docker-context-inspection.trx"
    for part in (path, *path.parents):
        if part.is_symlink() or getattr(part, "is_junction", lambda: False)():
            raise ValueError("Linked inspection evidence denied")
    with path.open("rb") as stream: data = stream.read(MAX_BYTES + 1)
    validate_inspection_bytes(data, specification)

if __name__ == "__main__": main()
