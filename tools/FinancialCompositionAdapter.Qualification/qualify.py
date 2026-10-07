"""Compile-only qualification. Captured source/config/compiler payloads never leave private memory."""
import hashlib
import json
import os
import pathlib
import subprocess
import sys
import copy
import re

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


def clean_audit(raw, expected_project):
    # Same primary NuGet JSON v1 contract used by the existing counter qualification.
    value = strict_json(raw)
    require(type(value) is dict and set(value) in (
        {"version", "parameters", "sources", "projects"},
        {"version", "parameters", "sources", "projects", "problems"}))
    require(type(value["version"]) is int and value["version"] == 1)
    require(value["parameters"] == "--vulnerable --include-transitive")
    require(value["sources"] == ["https://api.nuget.org/v3/index.json"])
    require(type(value.get("problems", [])) is list and value.get("problems", []) == [])
    require(type(value["projects"]) is list and len(value["projects"]) == 1)
    project = value["projects"][0]
    require(type(project) is dict and set(project) == {"path", "frameworks"} and project["path"] == expected_project)
    require(type(project["frameworks"]) is list and len(project["frameworks"]) == 1)
    framework = project["frameworks"][0]
    require(type(framework) is dict and set(framework) in (
        {"framework", "topLevelPackages"}, {"framework", "topLevelPackages", "transitivePackages"}))
    require(framework["framework"] == "net10.0")
    require(type(framework["topLevelPackages"]) is list and framework["topLevelPackages"] == [])
    require(type(framework.get("transitivePackages", [])) is list and framework.get("transitivePackages", []) == [])


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
    for selected in ("tools/FinancialCompositionAdapter/FinancialCompositionAdapter.csproj", project):
        begin_stage("controls-audit" if selected == project else "library-audit")
        expected = str(root / selected)
        clean_audit(run(["dotnet", "package", "list", "--project", expected, "--vulnerable", "--include-transitive", "--no-restore", "--format", "json", "--output-version", "1"], env=env), expected)
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
    receipt = {"sourceHead": pr["head"]["sha"], "pullRequestNumber": event["number"], "runId": os.environ["GITHUB_RUN_ID"],
               "runAttempt": os.environ["GITHUB_RUN_ATTEMPT"], "sourceManifestSha256": digest(root / "tools/FinancialCompositionAdapter.Qualification/source-manifest.json"),
               "acceptedFixtureCommit": FIXTURE_COMMIT, "acceptedFixtureRun": 37655912717, "sourceArchives": archives,
               "materializerIdentity": materializer_identity,
               "sameRunLockDigests": locks, "committedLockAcceptance": False, "assemblySha256": assemblies, "auditParserControls": parser_controls,
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
    except Exception:
        # No exception payload, configuration, archive contents or compiler output in public logs.
        print(json.dumps({"QualificationFailed": True, "FailedStage": ACTIVE_STAGE}), flush=True)
        sys.exit(1)
