"""Exact source-bound native controls; real SDK signatures remain distinct from live backend proof."""
import hashlib
import json
from pathlib import Path
from check_c821_focused_results import validate


def main():
    repository = Path(__file__).resolve().parents[1]
    inventory = json.loads((repository / "scripts/companion-focused-inventory.json").read_text())
    if set(inventory) != {"admission", "signing"}:
        raise ValueError("Exact companion native inventories required")
    for specification in inventory.values():
        source = repository / specification["source"]
        if hashlib.sha256(source.read_bytes().replace(b"\r\n", b"\n")).hexdigest() != specification["normalizedSourceSha256"]:
            raise ValueError("Reviewed companion native inventory changed")
        validate(repository / specification["trx"], specification, repository / "TestResults")


if __name__ == "__main__":
    main()
