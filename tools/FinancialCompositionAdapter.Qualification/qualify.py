"""Compile-only qualification. Captured source/config/compiler payloads never leave private memory."""
import hashlib
import json
import os
import pathlib
import subprocess
import sys
import copy
import re
import xml.etree.ElementTree as ET

AUDIT_GRAPH = {'IamDomain': {'path': '.adapter-dependencies/AcceptedAuth/.genuine-iam-source/Maliev.IAMService/Maliev.IAMService.Domain/Maliev.IAMService.Domain.csproj', 'sha256': '0aec8d23fc31d062753209091db0c1b51aef9b67e0ed3626bf0dee66e38dc118', 'packageFree': True, 'references': [], 'directPackages': {}}, 'IamApplication': {'path': '.adapter-dependencies/AcceptedAuth/.genuine-iam-source/Maliev.IAMService/Maliev.IAMService.Application/Maliev.IAMService.Application.csproj', 'sha256': '213e27a03edf02477c105ac518a5118529a6b7f889b66dd9409eeadaacdbdbf5', 'packageFree': False, 'references': ['IamDomain', 'Messaging', 'AspireDefaults'], 'directPackages': {'MassTransit': '[8.5.8, 9.0.0)', 'Microsoft.AspNetCore.Cryptography.KeyDerivation': '10.0.5', 'Microsoft.Extensions.Caching.Abstractions': '10.0.5', 'Microsoft.Extensions.Configuration.Abstractions': '10.0.7', 'Microsoft.Extensions.Logging.Abstractions': '10.0.7', 'Microsoft.Extensions.Configuration.Binder': '10.0.5', 'Microsoft.IdentityModel.Tokens': '8.16.0', 'System.IdentityModel.Tokens.Jwt': '8.16.0', 'Microsoft.AspNetCore.WebUtilities': '10.0.5', 'StackExchange.Redis': '2.12.1'}}, 'IamInfrastructure': {'path': '.adapter-dependencies/AcceptedAuth/.genuine-iam-source/Maliev.IAMService/Maliev.IAMService.Infrastructure/Maliev.IAMService.Infrastructure.csproj', 'sha256': 'acc757189230122c3f1ec15f8733d264c899472dd16f59038367d2a1a4a25f24', 'packageFree': False, 'references': ['IamApplication'], 'directPackages': {'Npgsql.EntityFrameworkCore.PostgreSQL': '10.0.1', 'Microsoft.EntityFrameworkCore.Design': '10.0.5'}}, 'AspireDefaults': {'path': '.adapter-dependencies/AcceptedAuth/.genuine-iam-source/Maliev.Aspire/Maliev.Aspire.ServiceDefaults/Maliev.Aspire.ServiceDefaults.csproj', 'sha256': 'b7b211fe3c424ed49336fb830f2f76b0b1f9323853fd28250d83ddd2a3d4d8f8', 'packageFree': False, 'references': ['Messaging'], 'directPackages': {'AspNetCore.HealthChecks.Rabbitmq': '9.0.0', 'AspNetCore.HealthChecks.Redis': '9.0.0', 'Microsoft.Extensions.Http.Resilience': '10.4.0', 'Microsoft.Extensions.ServiceDiscovery': '10.4.0', 'OpenTelemetry.Exporter.OpenTelemetryProtocol': '1.15.3', 'OpenTelemetry.Exporter.Prometheus.AspNetCore': '1.14.0-beta.1', 'OpenTelemetry.Extensions.Hosting': '1.15.3', 'OpenTelemetry.Instrumentation.AspNetCore': '1.15.1', 'OpenTelemetry.Instrumentation.Http': '1.15.1', 'OpenTelemetry.Instrumentation.Runtime': '1.15.1', 'MassTransit.RabbitMQ': '[8.5.8, 9.0.0)', 'MassTransit.Abstractions': '[8.5.8, 9.0.0)', 'Microsoft.Extensions.Caching.StackExchangeRedis': '10.0.5', 'Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore': '10.0.5', 'Npgsql.EntityFrameworkCore.PostgreSQL': '10.0.1', 'Microsoft.AspNetCore.Authentication.JwtBearer': '10.0.5', 'System.IdentityModel.Tokens.Jwt': '8.16.0', 'Microsoft.AspNetCore.OpenApi': '10.0.5', 'Microsoft.OpenApi': '2.7.5', 'Scalar.AspNetCore': '2.13.10', 'Asp.Versioning.Mvc.ApiExplorer': '8.1.1'}}, 'Messaging': {'path': '.adapter-dependencies/AcceptedAuth/.genuine-iam-source/Maliev.MessagingContracts/generated/csharp/Maliev.MessagingContracts.csproj', 'sha256': '08089949de3ba41e6d62771b09563d018719f9d34a1f3bfc634e460efa2d7007', 'packageFree': True, 'references': [], 'directPackages': {}}, 'Adapter': {'path': 'tools/FinancialCompositionAdapter/FinancialCompositionAdapter.csproj', 'sha256': '911d16013f0bd904f0815f8bb2985d153beb03ed64d7fa9e8cfadd904ef461bc', 'packageFree': True, 'references': ['IamApplication', 'IamInfrastructure'], 'directPackages': {}}, 'Controls': {'path': 'tools/FinancialCompositionAdapter.Controls/AdapterCompileControls.csproj', 'sha256': '84ee70cb4a70d11a2c6f31725cdd11c1a5abcb04ea6963cce91147b19d7fb336', 'packageFree': True, 'references': ['Adapter', 'IamApplication', 'IamInfrastructure'], 'directPackages': {}}}
AUDIT_GRAPH_SOURCE_INPUTS = {'.adapter-dependencies/AcceptedAuth/.genuine-iam-source/Maliev.IAMService/Directory.Build.props': '75382c0f97e85f1d3789ffa5e4483e374ee8fe625fc062bdb055d096fae9e6b4', '.adapter-dependencies/AcceptedAuth/.genuine-iam-source/Maliev.Aspire/Directory.Build.props': '0984531cdb437001ea6bc73eaabfd9472f88f12510ecbd7c90dcd14130775574'}
AUDIT_CONFIG_BYTES = b'<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources>\n    <clear />\n    <add key="public" value="https://api.nuget.org/v3/index.json" />\n  </packageSources>\n  <auditSources>\n    <clear />\n    <add key="public" value="https://api.nuget.org/v3/index.json" />\n  </auditSources>\n</configuration>\n'
AUDIT_CONFIG_SHA256 = '1c456df63f1ee6da160cdd43c69b1aa2b8d24312ddb25eedcbd0249eeb2fd3c2'


SOURCE_NAMES = (
    "Maliev.IAMService", "Maliev.Aspire", "Maliev.MessagingContracts",
    "Legacy.Maliev.AuthService", "Legacy.Maliev.ServiceDefaults", "Legacy.Maliev.CompatibilityContracts",
)
SOURCE_COMMITS = (
    "4fe6642e5674013de9a3672505aec898fdcae0ed", "01d506203763b914e237268a8746f1406423df86",
    "559a00db0c7920a5247fdff60d4476ad23a9a501", "88e430946465a1df6238e10b815d495d43453411",
    "7b3099bf67d0f17e56cfdb3dcf36541304abaac2", "78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7",
)
PINS = dict(zip(SOURCE_NAMES, SOURCE_COMMITS, strict=True))
FIXTURE_COMMIT = "85a00d4bb54cf95199ee67dfd97d2b168233ff68"
CONTROLS = [
    "OriginalThreeSegmentGrammar", "TypedCataloguePreservesLiteral",
    "FourSegmentRefusesWholeCatalogue", "DuplicateLiteralRefused",
    "UnpinnedWitnessRefused", "SubstitutedPrincipalServiceRefusedBeforeDatabase",
    "SubstitutedPrincipalRepositoryRefusedBeforeDatabase", "NoSubstitutedDependencyInvoked",
]


STAGES = (
    'source-assets-lock-graph-admission', 'graph-owner-audit-IamDomain', 'graph-owner-audit-IamApplication', 'graph-owner-audit-IamInfrastructure', 'graph-owner-audit-AspireDefaults', 'graph-owner-audit-Messaging', 'graph-owner-audit-Adapter', 'graph-owner-audit-Controls',
    "bootstrap", "initial-restore", "same-run-lock-discovery", "locked-restore", "locked-restore-digest-verification",
    "library-audit", "controls-audit", "release-build", "library-format", "controls-format",
    "compile-controls", "gitleaks", "source-and-lock-readback", "assembly-readback", "public-receipt",
)
ACTIVE_STAGE = "bootstrap"
NUGET_CODES = frozenset((
    "NU1000", "NU1001", "NU1002", "NU1003", "NU1004", "NU1005", "NU1006", "NU1007", "NU1008", "NU1009", "NU1010", "NU1011", "NU1012", "NU1013",
    "NU1100", "NU1101", "NU1102", "NU1103", "NU1104", "NU1105", "NU1106", "NU1107", "NU1108", "NU1109", "NU1110",
    "NU1201", "NU1202", "NU1203", "NU1204", "NU1211", "NU1212", "NU1301", "NU1302", "NU1401", "NU1402", "NU1403",
    "NU1501", "NU1502", "NU1503", "NU1504", "NU1505", "NU1506", "NU1507", "NU1508", "NU1509", "NU1510",
    "NU1601", "NU1602", "NU1603", "NU1604", "NU1605", "NU1606", "NU1607", "NU1608", "NU1701", "NU1702", "NU1801", "NU1900", "NU1901", "NU1902", "NU1903", "NU1904", "NU1905",
))
OWNED_SOURCES = (
    "tools/FinancialCompositionAdapter/OrdinaryOpaquePrincipalEnrollment.cs",
    "tools/FinancialCompositionAdapter/OwnedBusinessSchema.cs",
    "tools/FinancialCompositionAdapter/PinnedBusinessSchemas.g.cs",
    "tools/FinancialCompositionAdapter/SourceCatalogueAdmission.cs",
    "tools/FinancialCompositionAdapter/FinancialCompositionAdapter.csproj",
    "tools/FinancialCompositionAdapter.Controls/Program.cs",
    "tools/FinancialCompositionAdapter.Controls/AdapterCompileControls.csproj",
)
DIAGNOSTIC_STAGES = frozenset(("initial-restore", "locked-restore", "library-audit", "controls-audit", "release-build", "library-format", "controls-format"))


def begin_stage(stage):
    global ACTIVE_STAGE
    require(stage in STAGES)
    ACTIVE_STAGE = stage
    print(json.dumps({"QualificationStage": stage, "State": "begin"}), flush=True)


def public_diagnostics(raw, root, stage):
    # Reuses the counter qualifier's source-bound coordinate projection, with stricter NuGet code allowlisting.
    # Captured paths/messages/exception values never leave this function.
    if stage not in DIAGNOSTIC_STAGES:
        return {"NuGetCodes": [], "OwnedSourceDiagnostics": []}
    require(type(raw) is bytes and len(raw) <= 8 * 1024 * 1024)
    nuget = sorted({code.decode("ascii") for code in re.findall(rb"\b(?:error|warning)\s+(NU[0-9]{4})\b", raw, re.I)
                    if code.decode("ascii") in NUGET_CODES})[:32]
    locations = set()
    code_pattern = rb"(?:WHITESPACE|(?:CS|MSB|CA|IDE)[0-9]{3,6})"
    for relative in OWNED_SOURCES:
        for candidate in (str(root / relative), relative):
            pattern = rb"(?:^|[\r\n])" + re.escape(candidate.encode()) + rb"\(([0-9]{1,7}),([0-9]{1,7})\):\s*(?:error|warning)\s+(" + code_pattern + rb")\b"
            for match in re.finditer(pattern, raw):
                line, column = int(match[1]), int(match[2])
                if 0 < line <= 1000000 and 0 < column <= 1000000:
                    locations.add((relative, line, column, match[3].decode("ascii")))
    return {"NuGetCodes": nuget, "OwnedSourceDiagnostics": [
        {"Source": path, "Line": line, "Column": column, "Code": code}
        for path, line, column, code in sorted(locations)[:32]]}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require(value):
    if not value:
        raise RuntimeError("qualification predicate")


def strict_json(raw):
    require(type(raw) is bytes and 0 < len(raw) <= 1024 * 1024)

    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result)
            result[key] = value
        return result

    def constant(_):
        raise ValueError("non-finite JSON")

    return json.loads(raw.decode("utf-8"), object_pairs_hook=pairs, parse_constant=constant)


AUDIT_REJECTION_CATEGORIES = ('JsonContractRejected', 'TopLevelShapeRejected', 'VersionRejected', 'ParametersRejected', 'SourcesRejected', 'ProblemsRejected', 'ProjectInventoryRejected', 'ProjectShapeOrPathRejected', 'FrameworkInventoryRejected', 'FrameworkShapeRejected', 'FrameworkIdentityRejected', 'TopLevelPackageInventoryRejected', 'TransitivePackageInventoryRejected')


class AuditRejected(RuntimeError):
    def __init__(self, category, evidence=None):
        require(category in AUDIT_REJECTION_CATEGORIES)
        evidence = {} if evidence is None else evidence
        require(type(evidence) is dict)
        require(not evidence or (category == "ProjectShapeOrPathRejected"
                                and set(evidence) == {"ProjectPathMatches", "ProjectFrameworksPresent", "ProjectShapeKnown"}
                                and all(type(value) is bool for value in evidence.values())))
        self.category = category
        self.evidence = dict(evidence)
        super().__init__("audit contract rejected")


def audit_require(value, category, evidence=None):
    require(category in AUDIT_REJECTION_CATEGORIES)
    if not value:
        raise AuditRejected(category, evidence)


def verify_audit_configuration(raw):
    require(type(raw) is bytes and raw == AUDIT_CONFIG_BYTES)
    require(hashlib.sha256(raw).hexdigest() == AUDIT_CONFIG_SHA256)


GRAPH_REJECTION_CATEGORIES = ('GraphEntryRejected', 'RequestedRangeTypeRejected', 'RequestedRangeSyntaxRejected', 'AdmissionArgumentTypesRejected', 'SourceDirectClassificationRejected', 'AssetsVersionRejected', 'AssetsProjectIdentityRejected', 'AssetsOriginalFrameworkRejected', 'AssetsRestoreFrameworkRejected', 'ProjectReferenceInventoryRejected', 'ProjectReferenceIdentityRejected', 'AssetsProjectFrameworkRejected', 'AssetsDirectDependencyShapeRejected', 'AssetsDirectClassificationRejected', 'SourceDirectDuplicateRejected', 'AssetsSourceDirectInventoryRejected', 'AssetsSourceDirectRangeRejected', 'LockVersionRejected', 'LockFrameworkRejected', 'LockDependencyShapeRejected', 'LockDependencyTypeRejected', 'LockDirectDuplicateRejected', 'LockAssetsDirectInventoryRejected', 'LockSourceDirectRangeRejected', 'LockResolvedVersionRejected', 'AssetsLibraryShapeRejected', 'AssetsLibraryTypeRejected', 'LockAssetsPackageInventoryRejected', 'ImportedSourceDigestRejected', 'SevenLockOwnerInventoryRejected', 'ProjectSourceDigestRejected', 'SourcePackageClassificationRejected', 'SameRunLockDigestRejected', 'GlobalAuditedPackageClosureRejected')
GRAPH_REJECTION_ROLES = ("Graph", *AUDIT_GRAPH)
GRAPH_ROLE = "Graph"
GRAPH_POINT = "GraphEntryRejected"

class GraphRejected(RuntimeError):
    def __init__(self, role, category, kind):
        require(role in GRAPH_REJECTION_ROLES and category in GRAPH_REJECTION_CATEGORIES)
        require(kind in ("PredicateRejected", "DataAccessRejected"))
        self.role = role
        self.category = category
        self.kind = kind
        super().__init__("graph contract rejected")


def graph_point(category):
    global GRAPH_POINT
    require(category in GRAPH_REJECTION_CATEGORIES)
    GRAPH_POINT = category


def graph_require(value, category):
    graph_point(category)
    if not value:
        raise GraphRejected(GRAPH_ROLE, category, "PredicateRejected")


def graph_role(role):
    global GRAPH_ROLE
    require(role in GRAPH_REJECTION_ROLES)
    GRAPH_ROLE = role
    graph_point("GraphEntryRejected")


def graph_guard(function):
    def guarded(*args, **kwargs):
        try:
            return function(*args, **kwargs)
        except GraphRejected:
            raise
        except Exception:
            # No key, path, payload, exception type or exception string leaves private memory.
            raise GraphRejected(GRAPH_ROLE, GRAPH_POINT, "DataAccessRejected") from None
    return guarded


@graph_guard
def requested_range(value):
    graph_point('RequestedRangeTypeRejected')
    graph_require(type(value) is str, 'RequestedRangeTypeRejected')
    value = value.replace(" ", "")
    if re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", value):
        return "[" + value + ",)"
    graph_point('RequestedRangeSyntaxRejected')
    graph_require(re.fullmatch(r"\[[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?,(?:[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)?\)", value) is not None, 'RequestedRangeSyntaxRejected')
    return value


@graph_guard
def admit_project_assets(assets, lock, expected_path, package_free, expected_references, expected_dependencies):
    graph_point('AdmissionArgumentTypesRejected')
    graph_require(type(package_free) is bool and type(expected_references) is set, 'AdmissionArgumentTypesRejected')
    graph_point('SourceDirectClassificationRejected')
    graph_require(type(expected_dependencies) is dict and (not expected_dependencies) == package_free, 'SourceDirectClassificationRejected')
    graph_point('AssetsVersionRejected')
    graph_require(type(assets) is dict and type(assets.get("version")) is int and assets["version"] == 3, 'AssetsVersionRejected')
    project = assets["project"]
    restore = project["restore"]
    graph_point('AssetsProjectIdentityRejected')
    graph_require(restore["projectPath"] == expected_path and restore["projectUniqueName"] == expected_path, 'AssetsProjectIdentityRejected')
    graph_point('AssetsOriginalFrameworkRejected')
    graph_require(restore["originalTargetFrameworks"] == ["net10.0"], 'AssetsOriginalFrameworkRejected')
    graph_point('AssetsRestoreFrameworkRejected')
    graph_require(type(restore["frameworks"]) is dict and set(restore["frameworks"]) == {"net10.0"}, 'AssetsRestoreFrameworkRejected')
    references = restore["frameworks"]["net10.0"].get("projectReferences", {})
    graph_point('ProjectReferenceInventoryRejected')
    graph_require(type(references) is dict and set(references) == expected_references, 'ProjectReferenceInventoryRejected')
    graph_point('ProjectReferenceIdentityRejected')
    graph_require(all(type(value) is dict and value.get("projectPath") == key for key, value in references.items()), 'ProjectReferenceIdentityRejected')
    graph_point('AssetsProjectFrameworkRejected')
    graph_require(type(project["frameworks"]) is dict and set(project["frameworks"]) == {"net10.0"}, 'AssetsProjectFrameworkRejected')
    dependencies = project["frameworks"]["net10.0"].get("dependencies", {})
    graph_point('AssetsDirectDependencyShapeRejected')
    graph_require(type(dependencies) is dict and all(type(key) is str and type(value) is dict for key, value in dependencies.items()), 'AssetsDirectDependencyShapeRejected')
    graph_point('AssetsDirectClassificationRejected')
    graph_require((not dependencies) == package_free, 'AssetsDirectClassificationRejected')
    expected_ranges = {key.casefold(): value for key, value in expected_dependencies.items()}
    graph_point('SourceDirectDuplicateRejected')
    graph_require(len(expected_ranges) == len(expected_dependencies), 'SourceDirectDuplicateRejected')
    graph_point('AssetsSourceDirectInventoryRejected')
    graph_require({key.casefold() for key in dependencies} == set(expected_ranges) and len(dependencies) == len(expected_ranges), 'AssetsSourceDirectInventoryRejected')
    graph_point('AssetsSourceDirectRangeRejected')
    graph_require(all(requested_range(value["version"]) == requested_range(expected_ranges[key.casefold()]) for key, value in dependencies.items()), 'AssetsSourceDirectRangeRejected')
    graph_point('LockVersionRejected')
    graph_require(type(lock) is dict and type(lock.get("version")) is int and lock["version"] in (1, 2), 'LockVersionRejected')
    graph_point('LockFrameworkRejected')
    graph_require(type(lock["dependencies"]) is dict and set(lock["dependencies"]) == {"net10.0"}, 'LockFrameworkRejected')
    locked = lock["dependencies"]["net10.0"]
    graph_point('LockDependencyShapeRejected')
    graph_require(type(locked) is dict and all(type(key) is str and type(value) is dict for key, value in locked.items()), 'LockDependencyShapeRejected')
    graph_point('LockDependencyTypeRejected')
    graph_require(all(value.get("type") in ("Direct", "Transitive", "Project") for value in locked.values()), 'LockDependencyTypeRejected')
    direct = {key.casefold() for key, value in locked.items() if value["type"] == "Direct"}
    graph_point('LockDirectDuplicateRejected')
    graph_require(len(direct) == sum(value["type"] == "Direct" for value in locked.values()), 'LockDirectDuplicateRejected')
    graph_point('LockAssetsDirectInventoryRejected')
    graph_require(direct == {key.casefold() for key in dependencies} and len(direct) == len(dependencies), 'LockAssetsDirectInventoryRejected')
    graph_point('LockSourceDirectRangeRejected')
    graph_require(all(requested_range(value["requested"]) == requested_range(expected_ranges[key.casefold()]) for key, value in locked.items() if value["type"] == "Direct"), 'LockSourceDirectRangeRejected')
    packages = set()
    for name, value in locked.items():
        if value["type"] != "Project":
            graph_point('LockResolvedVersionRejected')
            graph_require(type(value.get("resolved")) is str and bool(value["resolved"]), 'LockResolvedVersionRejected')
            packages.add(name.casefold() + "/" + value["resolved"].casefold())
    libraries = assets["libraries"]
    graph_point('AssetsLibraryShapeRejected')
    graph_require(type(libraries) is dict and all(type(key) is str and type(value) is dict for key, value in libraries.items()), 'AssetsLibraryShapeRejected')
    graph_point('AssetsLibraryTypeRejected')
    graph_require(all(value.get("type") in ("package", "project") for value in libraries.values()), 'AssetsLibraryTypeRejected')
    actual_packages = {key.casefold() for key, value in libraries.items() if value["type"] == "package"}
    graph_point('LockAssetsPackageInventoryRejected')
    graph_require(actual_packages == packages and len(actual_packages) == sum(value["type"] == "package" for value in libraries.values()), 'LockAssetsPackageInventoryRejected')
    return packages


@graph_guard
def admit_audit_graph(root, locks):
    graph_role("Graph")
    graph_point('ImportedSourceDigestRejected')
    graph_require(all(digest(root / path) == expected for path, expected in AUDIT_GRAPH_SOURCE_INPUTS.items()), 'ImportedSourceDigestRejected')
    projects = {name: root / item["path"] for name, item in AUDIT_GRAPH.items()}
    expected_locks = {str(path.parent.joinpath("packages.lock.json").relative_to(root)) for path in projects.values()}
    graph_point('SevenLockOwnerInventoryRejected')
    graph_require(set(locks) == expected_locks and len(expected_locks) == 7, 'SevenLockOwnerInventoryRejected')
    packages = {}
    records = {}
    for name, item in AUDIT_GRAPH.items():
        graph_role(name)
        path = projects[name]
        graph_point('ProjectSourceDigestRejected')
        graph_require(path.is_file() and digest(path) == item["sha256"], 'ProjectSourceDigestRejected')
        xml = ET.fromstring(path.read_bytes())
        source_references = [element for element in xml.iter() if element.tag == "PackageReference"]
        graph_point('SourcePackageClassificationRejected')
        graph_require((not source_references) == item["packageFree"], 'SourcePackageClassificationRejected')
        lock_path = path.parent / "packages.lock.json"
        graph_point('SameRunLockDigestRejected')
        graph_require(digest(lock_path) == locks[str(lock_path.relative_to(root))], 'SameRunLockDigestRejected')
        assets_path = path.parent / "obj/project.assets.json"
        assets = strict_json(assets_path.read_bytes())
        lock = strict_json(lock_path.read_bytes())
        expected_refs = {str(projects[reference]) for reference in item["references"]}
        packages[name] = admit_project_assets(assets, lock, str(path), item["packageFree"], expected_refs, item["directPackages"])
        records[name] = {"projectSourceSha256": item["sha256"], "assetsSha256": digest(assets_path),
                         "sameRunLockSha256": digest(lock_path), "packageFree": item["packageFree"],
                         "packageCount": len(packages[name]), "framework": "net10.0"}
    graph_role("Graph")
    covered = set().union(*(packages[name] for name, item in AUDIT_GRAPH.items() if not item["packageFree"]))
    graph_point('GlobalAuditedPackageClosureRejected')
    graph_require(all(value <= covered for value in packages.values()), 'GlobalAuditedPackageClosureRejected')
    return records


def verify_graph_assets_unchanged(root, records):
    require(all(digest(root / path) == expected for path, expected in AUDIT_GRAPH_SOURCE_INPUTS.items()))
    require(set(records) == set(AUDIT_GRAPH))
    for name, item in AUDIT_GRAPH.items():
        path = root / item["path"]
        require(digest(path) == records[name]["projectSourceSha256"])
        require(digest(path.parent / "obj/project.assets.json") == records[name]["assetsSha256"])
        require(digest(path.parent / "packages.lock.json") == records[name]["sameRunLockSha256"])


def clean_audit(raw, expected_project, *, package_free=False):
    require(type(package_free) is bool)
    # Same primary NuGet JSON v1 contract used by the existing counter qualification.
    try:
        value = strict_json(raw)
    except (RuntimeError, ValueError, TypeError, UnicodeError):
        raise AuditRejected("JsonContractRejected") from None
    audit_require(type(value) is dict and set(value) in (
        {"version", "parameters", "sources", "projects"},
        {"version", "parameters", "sources", "projects", "problems"}), "TopLevelShapeRejected")
    audit_require(type(value["version"]) is int and value["version"] == 1, "VersionRejected")
    audit_require(value["parameters"] == "--vulnerable --include-transitive", "ParametersRejected")
    audit_require(value["sources"] == ["https://api.nuget.org/v3/index.json"], "SourcesRejected")
    audit_require(type(value.get("problems", [])) is list and value.get("problems", []) == [], "ProblemsRejected")
    audit_require(type(value["projects"]) is list and len(value["projects"]) == 1, "ProjectInventoryRejected")
    project = value["projects"][0]
    project_evidence = {
        "ProjectPathMatches": type(project) is dict and project.get("path") == expected_project,
        "ProjectFrameworksPresent": type(project) is dict and "frameworks" in project,
        "ProjectShapeKnown": type(project) is dict and set(project) in ({"path"}, {"path", "frameworks"}),
    }
    if package_free and type(project) is dict and set(project) == {"path"} and project["path"] == expected_project:
        return
    audit_require(type(project) is dict and set(project) == {"path", "frameworks"} and project["path"] == expected_project, "ProjectShapeOrPathRejected", project_evidence)
    audit_require(type(project["frameworks"]) is list and len(project["frameworks"]) == 1, "FrameworkInventoryRejected")
    framework = project["frameworks"][0]
    audit_require(type(framework) is dict and set(framework) in (
        {"framework", "topLevelPackages"}, {"framework", "topLevelPackages", "transitivePackages"}), "FrameworkShapeRejected")
    audit_require(framework["framework"] == "net10.0", "FrameworkIdentityRejected")
    audit_require(type(framework["topLevelPackages"]) is list and framework["topLevelPackages"] == [], "TopLevelPackageInventoryRejected")
    audit_require(type(framework.get("transitivePackages", [])) is list and framework.get("transitivePackages", []) == [], "TransitivePackageInventoryRejected")


def audit_parser_controls():
    positive = {"version": 1, "parameters": "--vulnerable --include-transitive",
                "sources": ["https://api.nuget.org/v3/index.json"], "projects": [
                    {"path": "/owned/project.csproj", "frameworks": [{"framework": "net10.0", "topLevelPackages": []}]}]}
    encoded = lambda value: json.dumps(value).encode()
    clean_audit(encoded(positive), "/owned/project.csproj")
    complete = copy.deepcopy(positive)
    complete["problems"] = []
    complete["projects"][0]["frameworks"][0]["transitivePackages"] = []
    clean_audit(encoded(complete), "/owned/project.csproj")
    negatives = [b"", b"prefix" + encoded(positive), encoded(positive) + b"{}",
                 encoded(positive).replace(b'"version": 1', b'"version": 1, "version": 1', 1), b'{"version":NaN}']

    def variant(change):
        value = copy.deepcopy(positive)
        change(value)
        negatives.append(encoded(value))

    variant(lambda value: value.update(version=True))
    variant(lambda value: value.update(parameters="--vulnerable"))
    variant(lambda value: value.update(sources=["https://private.invalid/index.json"]))
    variant(lambda value: value.update(problems=[{"level": "warning", "text": "audit unavailable"}]))
    variant(lambda value: value.update(unknown=[]))
    variant(lambda value: value.update(projects=[]))
    variant(lambda value: value["projects"][0].update(path="/other/project.csproj"))
    variant(lambda value: value["projects"][0]["frameworks"][0].update(framework="net9.0"))
    variant(lambda value: value["projects"][0]["frameworks"][0].update(topLevelPackages=[{"id": "vulnerable"}]))
    variant(lambda value: value["projects"][0]["frameworks"][0].update(transitivePackages=[{"id": "missing-details"}]))
    for raw in negatives:
        refused = False
        try:
            clean_audit(raw, "/owned/project.csproj")
        except (RuntimeError, ValueError, KeyError, TypeError, UnicodeError):
            refused = True
        require(refused)
    return {"positive": 2, "negative": len(negatives)}


def run(arguments, *, cwd=None, timeout=180, env=None):
    # No Docker, native hosts or background SDK servers; each command is awaited.
    try:
        result = subprocess.run(arguments, cwd=cwd, env=env, capture_output=True, timeout=timeout, check=False)
    except subprocess.TimeoutExpired:
        print(json.dumps({"QualificationStage": ACTIVE_STAGE, "FailureKind": "command-timeout"}), flush=True)
        raise
    require(len(result.stdout) <= 8 * 1024 * 1024 and len(result.stderr) <= 8 * 1024 * 1024)
    if result.returncode != 0:
        projection = public_diagnostics(result.stdout + b"\n" + result.stderr, pathlib.Path.cwd().resolve(), ACTIVE_STAGE)
        print(json.dumps({"QualificationStage": ACTIVE_STAGE, "FailureKind": "command-exit", **projection}), flush=True)
    require(result.returncode == 0)
    return result.stdout


def verify_materializer_identity(accepted, materializer, manifest):
    # Bind both immutable Git source and the exact checkout representation required by that source.
    # Do not rewrite the executed file or apply generic whitespace/line-ending normalization.
    relative = "eng/Prepare-GenuineIamHttpSource.ps1"
    expected_blob = manifest["acceptedMaterializerGitBlob"]
    require(run(["git", "-C", str(accepted), "rev-parse", FIXTURE_COMMIT + ":" + relative]).decode().strip() == expected_blob)
    blob = run(["git", "-C", str(accepted), "show", FIXTURE_COMMIT + ":" + relative])
    require(hashlib.sha256(blob).hexdigest() == manifest["acceptedMaterializerSha256"])
    attributes = accepted / ".gitattributes"
    require(digest(attributes) == manifest["acceptedAttributesSha256"])
    effective = run(["git", "-C", str(accepted), "check-attr", "-z", "text", "eol", "--", relative]).split(b"\0")
    require(effective == [relative.encode(), b"text", b"set", relative.encode(), b"eol", b"crlf", b""])
    checkout = materializer.read_bytes()
    require(hashlib.sha256(checkout).hexdigest() == manifest["acceptedMaterializerCheckoutSha256"])
    require(b"\r" not in blob and checkout == blob.replace(b"\n", b"\r\n"))
    return {"gitBlob": expected_blob, "blobSha256": hashlib.sha256(blob).hexdigest(),
            "checkoutSha256": hashlib.sha256(checkout).hexdigest(), "attributesSha256": digest(attributes),
            "effectiveText": "set", "effectiveEol": "crlf", "executedCheckoutUnmodified": True}


def main():
    root = pathlib.Path.cwd().resolve()
    parser_controls = audit_parser_controls()
    manifest = json.loads((root / "tools/FinancialCompositionAdapter.Qualification/source-manifest.json").read_text())
    event = json.loads(pathlib.Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
    pr = event["pull_request"]
    require(type(event["number"]) is int and event["number"] > 0 and pr["number"] == event["number"])
    require(pr["head"]["repo"]["full_name"] == os.environ["GITHUB_REPOSITORY"])
    require(pr["base"]["ref"] == "main" and pr["head"]["ref"] == "codex/financial-composition-compile-20261007")
    require(run(["git", "rev-parse", "HEAD"]).decode().strip() == pr["head"]["sha"])
    for file in manifest["files"]:
        path = root / file["destination"]
        require(path.is_file() and digest(path) == file["sha256"])
    print('{"Stage":"source-inventory","Passed":true}', flush=True)
    accepted = root / ".adapter-dependencies/AcceptedAuth"
    materializer = accepted / "eng/Prepare-GenuineIamHttpSource.ps1"
    require(run(["git", "-C", str(accepted), "rev-parse", "HEAD"]).decode().strip() == FIXTURE_COMMIT)
    materializer_identity = verify_materializer_identity(accepted, materializer, manifest)
    print('{"Stage":"materializer-source-and-checkout-identity","Passed":true}', flush=True)
    dependencies = accepted / ".dependencies"
    for name, pin in PINS.items():
        source = accepted if name == "Legacy.Maliev.AuthService" else dependencies / ("GenuineIamDefaults" if name == "Legacy.Maliev.ServiceDefaults" else name)
        require(run(["git", "-C", str(source), "cat-file", "-t", pin]).strip() == b"commit")
        if source != accepted:
            require(run(["git", "-C", str(source), "rev-parse", "HEAD"]).decode().strip() == pin)
    run(["pwsh", "-NoProfile", "-File", str(materializer), "-CommittedSourceRoot", str(dependencies)], timeout=120)
    genuine = accepted / ".genuine-iam-source"
    archives = json.loads((genuine / "source-manifest.json").read_text(encoding="utf-8-sig"))
    require(len(archives) == len(PINS) and {item["repository"]: item["commit"] for item in archives} == PINS)
    for item in archives:
        require(item["archiveSha256"] == digest(genuine / (item["repository"] + ".zip")))
    api = genuine / "Maliev.IAMService/Maliev.IAMService.Api/Program.cs"
    require(digest(api) == manifest["originalIamProgramDigest"])
    code = api.read_text()
    require("builder.Services.AddScoped<IPrincipalRepository, PrincipalRepository>();" in code)
    require("builder.Services.AddScoped<IPrincipalService, PrincipalService>();" in code)
    print('{"Stage":"exact-six-source-archives","Passed":true}', flush=True)
    env = dict(os.environ, GITHUB_ACTIONS="false", UseLocalMalievDependencies="true", MalievWorkspaceRoot=str(genuine), GenuineIamSourceRoot=str(genuine),
               DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    properties = ["-p:GITHUB_ACTIONS=false", "-p:UseLocalMalievDependencies=true", "-p:MalievWorkspaceRoot=" + str(genuine),
                  "-p:GenuineIamSourceRoot=" + str(genuine), "-p:UseSharedCompilation=false", "-nodeReuse:false", "-maxcpucount:1"]
    project = "tools/FinancialCompositionAdapter.Controls/AdapterCompileControls.csproj"
    # Initial immutable-source resolution is explicit; this is a same-run lock, not a committed-lock assertion.
    begin_stage("initial-restore")
    run(["dotnet", "restore", project, "--use-lock-file", *properties], env=env)
    begin_stage("same-run-lock-discovery")
    lockpaths = list(genuine.rglob("packages.lock.json")) + list((root / "tools/FinancialCompositionAdapter").rglob("packages.lock.json")) + list((root / "tools/FinancialCompositionAdapter.Controls").rglob("packages.lock.json"))
    print(json.dumps({"QualificationStage": ACTIVE_STAGE, "ObservedLockFileCount": min(len(lockpaths), 64)}), flush=True)
    require(len(lockpaths) >= 4)
    locks = {str(path.relative_to(root)): digest(path) for path in lockpaths}
    begin_stage("locked-restore")
    run(["dotnet", "restore", project, "--locked-mode", *properties], env=env)
    begin_stage("locked-restore-digest-verification")
    require(all(digest(root / path) == value for path, value in locks.items()))
    begin_stage("source-assets-lock-graph-admission")
    graph_records = admit_audit_graph(root, locks)
    audit_directory = root / ".adapter-dependencies/audit-only"
    audit_directory.mkdir(exist_ok=False)
    audit_config = audit_directory / "NuGet.Config"
    audit_config.write_bytes(AUDIT_CONFIG_BYTES)
    verify_audit_configuration(audit_config.read_bytes())
    for name, item in AUDIT_GRAPH.items():
        begin_stage("graph-owner-audit-" + name)
        expected = str(root / item["path"])
        verify_audit_configuration(audit_config.read_bytes())
        clean_audit(run(["dotnet", "package", "list", "--project", expected, "--config", str(audit_config), "--vulnerable", "--include-transitive", "--no-restore", "--format", "json", "--output-version", "1"], env=env), expected, package_free=item["packageFree"])
    verify_audit_configuration(audit_config.read_bytes())
    verify_graph_assets_unchanged(root, graph_records)
    print('{"Stage":"same-run-locked-restore-and-vulnerability-audit","Passed":true}', flush=True)
    begin_stage("release-build")
    run(["dotnet", "build", project, "--configuration", "Release", "--no-restore", "--warnaserror", *properties], env=env)
    for path in ("tools/FinancialCompositionAdapter/FinancialCompositionAdapter.csproj", project):
        begin_stage("controls-format" if path == project else "library-format")
        run(["dotnet", "format", path, "--verify-no-changes", "--no-restore", "--include-generated", "--include", "tools/FinancialCompositionAdapter", "tools/FinancialCompositionAdapter.Controls"], env=env)
    print('{"Stage":"release-build-and-strict-format","Passed":true}', flush=True)
    executable = root / "tools/FinancialCompositionAdapter.Controls/bin/Release/net10.0/AdapterCompileControls.dll"
    begin_stage("compile-controls")
    actual = strict_json(run(["dotnet", str(executable)], env=env, timeout=30))
    require(actual.get("Passed") == CONTROLS)
    for key in ("HostStarted", "DatabaseOpened", "EnrollmentAccepted", "FinancialEightAccepted"):
        require(actual.get(key) is False)
    begin_stage("gitleaks")
    run(["gitleaks", "git", "--redact", "--exit-code", "1", "--no-banner", "--log-opts=-1"], timeout=90)
    begin_stage("source-and-lock-readback")
    require(all(digest(root / file["destination"]) == file["sha256"] for file in manifest["files"]))
    require(all(digest(root / path) == value for path, value in locks.items()))
    begin_stage("assembly-readback")
    assemblies = {}
    for name in ("FinancialCompositionAdapter.dll", "AdapterCompileControls.dll", "Maliev.IAMService.Application.dll", "Maliev.IAMService.Infrastructure.dll"):
        path = executable.parent / name
        require(path.is_file() and 0 < path.stat().st_size <= 32 * 1024 * 1024)
        assemblies[name] = digest(path)
    verify_audit_configuration(audit_config.read_bytes())
    verify_graph_assets_unchanged(root, graph_records)
    receipt = {"sourceHead": pr["head"]["sha"], "pullRequestNumber": event["number"], "runId": os.environ["GITHUB_RUN_ID"],
               "runAttempt": os.environ["GITHUB_RUN_ATTEMPT"], "sourceManifestSha256": digest(root / "tools/FinancialCompositionAdapter.Qualification/source-manifest.json"),
               "acceptedFixtureCommit": FIXTURE_COMMIT, "acceptedFixtureRun": 37655912717, "sourceArchives": archives,
               "materializerIdentity": materializer_identity,
               "sameRunLockDigests": locks, "auditOnlyConfigSha256": AUDIT_CONFIG_SHA256, "auditedGraphOwners": graph_records, "auditedGraphOwnerCount": 7, "committedLockAcceptance": False, "assemblySha256": assemblies, "auditParserControls": parser_controls,
               "compileControls": actual, "compiled": True, "originalRuntimeDiRegistrationSourceWitness": True,
               "originalRuntimeDiHostStarted": False, "physicalBusinessSchemaAccepted": False, "principalEnrollmentAccepted": False,
               "catalogueRegistrationAccepted": False, "fileSigningAccepted": False, "financialEightAccepted": False}
    begin_stage("public-receipt")
    output = root / "artifacts/financial-composition-compile"
    output.mkdir(parents=True, exist_ok=False)
    (output / "qualification.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print('{"Stage":"compile-controls-security-and-public-receipt","Passed":true}', flush=True)


if __name__ == "__main__":
    try:
        main()
    except GraphRejected as rejection:
        print(json.dumps({"QualificationFailed": True, "FailedStage": ACTIVE_STAGE, "GraphRole": rejection.role,
                          "GraphRejectionCategory": rejection.category, "GraphFailureKind": rejection.kind}), flush=True)
        sys.exit(1)
    except AuditRejected as rejection:
        # Category values are a fixed public vocabulary, never actual JSON/configuration values.
        print(json.dumps({"QualificationFailed": True, "FailedStage": ACTIVE_STAGE, "AuditRejectionCategory": rejection.category, **rejection.evidence}), flush=True)
        sys.exit(1)
    except Exception:
        # No exception payload, configuration, archive contents or compiler output in public logs.
        print(json.dumps({"QualificationFailed": True, "FailedStage": ACTIVE_STAGE}), flush=True)
        sys.exit(1)
