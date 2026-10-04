"""Enforce QuotationService-owned handwritten line coverage from Cobertura XML."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


OWNED_ASSEMBLIES = (
    "Legacy.Maliev.QuotationService.Api",
    "Legacy.Maliev.QuotationService.Application",
    "Legacy.Maliev.QuotationService.Data",
    "Legacy.Maliev.QuotationService.Domain",
    "Legacy.Maliev.QuotationService.MigrationRunner",
)


@dataclass
class AssemblyCoverage:
    covered: int = 0
    total: int = 0
    generated_covered: int = 0
    generated_total: int = 0


def is_generated(filename: str) -> bool:
    """Exclude only SDK/source-generator output under an obj directory."""
    return "obj" in filename.replace("\\", "/").split("/")


def summarize(root: ET.Element) -> dict[str, AssemblyCoverage]:
    if root.tag != "coverage":
        raise ValueError("Expected a Cobertura coverage root")

    assemblies: dict[str, AssemblyCoverage] = {}
    for package in root.findall("./packages/package"):
        name = package.get("name")
        if not name or name in assemblies:
            raise ValueError("Coverage package name is missing or repeated")
        counts = AssemblyCoverage()
        for source_class in package.findall("./classes/class"):
            filename = source_class.get("filename")
            if not filename:
                raise ValueError(f"Coverage class in {name} has no filename")
            generated = is_generated(filename)
            for line in source_class.findall("./lines/line"):
                hits = int(line.attrib["hits"])
                if hits < 0:
                    raise ValueError("Coverage hits cannot be negative")
                if generated:
                    counts.generated_total += 1
                    counts.generated_covered += hits > 0
                else:
                    counts.total += 1
                    counts.covered += hits > 0
        assemblies[name] = counts
    return assemblies


def owned_totals(assemblies: dict[str, AssemblyCoverage], *, raw: bool = False) -> tuple[int, int]:
    missing = [name for name in OWNED_ASSEMBLIES if name not in assemblies or assemblies[name].total == 0]
    if missing:
        raise ValueError(f"Missing owned handwritten coverage: {', '.join(missing)}")
    return (
        sum(assemblies[name].covered + (assemblies[name].generated_covered if raw else 0) for name in OWNED_ASSEMBLIES),
        sum(assemblies[name].total + (assemblies[name].generated_total if raw else 0) for name in OWNED_ASSEMBLIES),
    )


def percentage(covered: int, total: int) -> float:
    return 100.0 * covered / total if total else 0.0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path, help="One Cobertura XML report from the complete test suite")
    parser.add_argument("--minimum", type=float, default=80.0, help="Minimum owned handwritten line percentage")
    parser.add_argument("--raw", action="store_true", help="Include generated lines and require each owned assembly to meet the minimum")
    args = parser.parse_args(argv)
    if not 0 <= args.minimum <= 100:
        parser.error("--minimum must be between 0 and 100")

    try:
        assemblies = summarize(ET.parse(args.report).getroot())
        covered, total = owned_totals(assemblies, raw=args.raw)
    except (ET.ParseError, OSError, KeyError, ValueError) as error:
        print(f"Coverage report invalid: {error}", file=sys.stderr)
        return 2

    print("Assembly | Handwritten covered/total (%) | Generated obj covered/total | Raw covered/total")
    for name, counts in sorted(assemblies.items()):
        raw_covered = counts.covered + counts.generated_covered
        raw_total = counts.total + counts.generated_total
        owner = "owned" if name in OWNED_ASSEMBLIES else "external"
        print(f"{name} [{owner}] | {counts.covered}/{counts.total} ({percentage(counts.covered, counts.total):.2f}%)"
              f" | {counts.generated_covered}/{counts.generated_total} | {raw_covered}/{raw_total}")

    actual = percentage(covered, total)
    mode = "raw" if args.raw else "handwritten"
    print(f"Owned {mode} total: {covered}/{total} ({actual:.2f}%); minimum {args.minimum:.2f}%")
    below = [name for name in OWNED_ASSEMBLIES if args.raw and percentage(
        assemblies[name].covered + assemblies[name].generated_covered,
        assemblies[name].total + assemblies[name].generated_total) < args.minimum]
    if actual < args.minimum or below:
        print(f"Owned {mode} coverage is below the required threshold" + (f": {', '.join(below)}" if below else ""), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
