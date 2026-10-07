"""Require all source-bound native observer controls; no financial acceptance claim."""
import hashlib
import json
from pathlib import Path

from check_storage_front_results import MAXIMUM, validate_bytes


def main():
    repository = Path(__file__).resolve().parents[1]
    inventory = json.loads((repository / "scripts/hosted-start-inventory.json").read_text())
    for group in inventory.values():
        source = repository / group["source"]
        if hashlib.sha256(source.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != group["normalizedSourceSha256"]:
            raise ValueError("Reviewed observer test source changed")
    path = repository / "TestResults/HostedStart/hosted-start.trx"
    for item in (path, *path.parents):
        if item.is_symlink() or getattr(item, "is_junction", lambda: False)():
            raise ValueError("Redirected native observer evidence denied")
    with path.open("rb") as stream:
        count = validate_bytes(stream.read(MAXIMUM + 1), inventory)
    print(f"{count} exact native observer controls passed; eight-host financial proof remains separate")


if __name__ == "__main__":
    main()
