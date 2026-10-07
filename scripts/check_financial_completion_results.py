"""Join original native completion controls to reviewed source identities; no financial runtime claim."""
import hashlib
import json
from pathlib import Path

from check_storage_front_results import MAXIMUM, validate_bytes


def main():
    repository = Path(__file__).resolve().parents[1]
    inventory = json.loads((repository / "scripts/financial-completion-inventory.json").read_text())
    for group in inventory.values():
        source = repository / group["source"]
        if hashlib.sha256(source.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != group["normalizedSourceSha256"]:
            raise ValueError("Reviewed financial completion native source changed")
    path = repository / "TestResults/FinancialCompletion/financial-completion.trx"
    for entry in (path, *path.parents):
        if entry.is_symlink() or getattr(entry, "is_junction", lambda: False)():
            raise ValueError("Redirected completion evidence denied")
    with path.open("rb") as stream:
        count = validate_bytes(stream.read(MAXIMUM + 1), inventory)
    print(f"financial-completion.trx: {count} exact native controls passed; genuine eight-host proof not established")


if __name__ == "__main__":
    main()
