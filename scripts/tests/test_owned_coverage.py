"""Executable pass/fail proof for the owner-scoped coverage gate."""

import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET


SCRIPT = Path(__file__).resolve().parents[1] / "check_owned_coverage.py"
SPEC = importlib.util.spec_from_file_location("check_owned_coverage", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
gate = importlib.util.module_from_spec(SPEC)
import sys
sys.modules[SPEC.name] = gate
SPEC.loader.exec_module(gate)


def report(api_hits: str, include_all_owned: bool = True) -> ET.Element:
    names = gate.OWNED_ASSEMBLIES if include_all_owned else gate.OWNED_ASSEMBLIES[:1]
    packages = []
    for name in names:
        hits = api_hits if name.endswith(".Api") else "1"
        second_line = f'<line number="2" hits="{hits}"/>' if name.endswith(".Api") else ""
        packages.append(f"""
            <package name="{name}"><classes>
              <class filename="src/{name}/Handwritten.cs"><lines><line number="1" hits="{hits}"/>{second_line}</lines></class>
              <class filename="src/{name}/obj/Release/Generated.cs"><lines><line number="1" hits="0"/></lines></class>
            </classes></package>""")
    packages.append("""
        <package name="Legacy.Maliev.ServiceDefaults"><classes>
          <class filename="shared/Code.cs"><lines><line number="1" hits="0"/></lines></class>
        </classes></package>""")
    return ET.fromstring("<coverage><packages>" + "".join(packages) + "</packages></coverage>")


class OwnedCoverageGateTests(unittest.TestCase):
    def test_raw_command_rejects_generated_gap_even_when_handwritten_passes(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "coverage.cobertura.xml"
            path.write_text(ET.tostring(report("1"), encoding="unicode"), encoding="utf-8")
            self.assertEqual(1, gate.main([str(path), "--minimum", "80", "--raw"]))

    def test_raw_gate_includes_every_generated_owned_line(self) -> None:
        assemblies = gate.summarize(report("1"))
        self.assertEqual((6, 11), gate.owned_totals(assemblies, raw=True))
        self.assertLess(gate.percentage(*gate.owned_totals(assemblies, raw=True)), 80)

    def test_owned_pass_excludes_visible_generated_and_external_lines(self) -> None:
        assemblies = gate.summarize(report("1"))
        self.assertEqual((6, 6), gate.owned_totals(assemblies))
        self.assertEqual(1, assemblies["Legacy.Maliev.QuotationService.Api"].generated_total)
        self.assertEqual(1, assemblies["Legacy.Maliev.ServiceDefaults"].total)
        self.assertGreaterEqual(gate.percentage(*gate.owned_totals(assemblies)), 80)

    def test_owned_fail_below_threshold(self) -> None:
        assemblies = gate.summarize(report("0"))
        self.assertEqual((4, 6), gate.owned_totals(assemblies))
        self.assertLess(gate.percentage(*gate.owned_totals(assemblies)), 80)

    def test_command_exits_fail_and_pass_for_same_report(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "coverage.cobertura.xml"
            path.write_text(ET.tostring(report("0"), encoding="unicode"), encoding="utf-8")
            self.assertEqual(1, gate.main([str(path), "--minimum", "80"]))
            path.write_text(ET.tostring(report("1"), encoding="unicode"), encoding="utf-8")
            self.assertEqual(0, gate.main([str(path), "--minimum", "80"]))

    def test_missing_owned_assembly_fails_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "Missing owned handwritten coverage"):
            gate.owned_totals(gate.summarize(report("1", include_all_owned=False)))

    def test_only_obj_path_is_generated(self) -> None:
        self.assertTrue(gate.is_generated(r"src\Api\obj\Release\Generated.cs"))
        self.assertFalse(gate.is_generated("src/Api/objects/Handwritten.cs"))


if __name__ == "__main__":
    unittest.main()
