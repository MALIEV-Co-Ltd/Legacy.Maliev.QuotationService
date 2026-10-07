"""Compile-only qualification. Captured source/config/compiler payloads never leave private memory."""
import hashlib
import json
import os
import pathlib
import subprocess
import sys
import copy

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
    result = subprocess.run(arguments, cwd=cwd, env=env, capture_output=True, timeout=timeout, check=False)
    require(len(result.stdout) <= 8 * 1024 * 1024 and len(result.stderr) <= 8 * 1024 * 1024)
    require(result.returncode == 0)
    return result.stdout


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
    require(digest(materializer) == manifest["acceptedMaterializerSha256"])
    require(run(["git", "-C", str(accepted), "rev-parse", "HEAD"]).decode().strip() == FIXTURE_COMMIT)
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
    run(["dotnet", "restore", project, "--use-lock-file", *properties], env=env)
    lockpaths = list(genuine.rglob("packages.lock.json")) + list((root / "tools/FinancialCompositionAdapter").rglob("packages.lock.json")) + list((root / "tools/FinancialCompositionAdapter.Controls").rglob("packages.lock.json"))
    require(len(lockpaths) >= 4)
    locks = {str(path.relative_to(root)): digest(path) for path in lockpaths}
    run(["dotnet", "restore", project, "--locked-mode", *properties], env=env)
    require(all(digest(root / path) == value for path, value in locks.items()))
    for selected in ("tools/FinancialCompositionAdapter/FinancialCompositionAdapter.csproj", project):
        expected = str(root / selected)
        clean_audit(run(["dotnet", "package", "list", "--project", expected, "--vulnerable", "--include-transitive", "--no-restore", "--format", "json", "--output-version", "1"], env=env), expected)
    print('{"Stage":"same-run-locked-restore-and-vulnerability-audit","Passed":true}', flush=True)
    run(["dotnet", "build", project, "--configuration", "Release", "--no-restore", "--warnaserror", *properties], env=env)
    for path in ("tools/FinancialCompositionAdapter/FinancialCompositionAdapter.csproj", project):
        run(["dotnet", "format", path, "--verify-no-changes", "--no-restore", "--include-generated", "--include", "tools/FinancialCompositionAdapter", "tools/FinancialCompositionAdapter.Controls"], env=env)
    print('{"Stage":"release-build-and-strict-format","Passed":true}', flush=True)
    executable = root / "tools/FinancialCompositionAdapter.Controls/bin/Release/net10.0/AdapterCompileControls.dll"
    actual = strict_json(run(["dotnet", str(executable)], env=env, timeout=30))
    require(actual.get("Passed") == CONTROLS)
    for key in ("HostStarted", "DatabaseOpened", "EnrollmentAccepted", "FinancialEightAccepted"):
        require(actual.get(key) is False)
    run(["gitleaks", "git", "--redact", "--exit-code", "1", "--no-banner", "--log-opts=-1"], timeout=90)
    require(all(digest(root / file["destination"]) == file["sha256"] for file in manifest["files"]))
    require(all(digest(root / path) == value for path, value in locks.items()))
    assemblies = {}
    for name in ("FinancialCompositionAdapter.dll", "AdapterCompileControls.dll", "Maliev.IAMService.Application.dll", "Maliev.IAMService.Infrastructure.dll"):
        path = executable.parent / name
        require(path.is_file() and 0 < path.stat().st_size <= 32 * 1024 * 1024)
        assemblies[name] = digest(path)
    receipt = {"sourceHead": pr["head"]["sha"], "pullRequestNumber": event["number"], "runId": os.environ["GITHUB_RUN_ID"],
               "runAttempt": os.environ["GITHUB_RUN_ATTEMPT"], "sourceManifestSha256": digest(root / "tools/FinancialCompositionAdapter.Qualification/source-manifest.json"),
               "acceptedFixtureCommit": FIXTURE_COMMIT, "acceptedFixtureRun": 37655912717, "sourceArchives": archives,
               "sameRunLockDigests": locks, "committedLockAcceptance": False, "assemblySha256": assemblies, "auditParserControls": parser_controls,
               "compileControls": actual, "compiled": True, "originalRuntimeDiRegistrationSourceWitness": True,
               "originalRuntimeDiHostStarted": False, "physicalBusinessSchemaAccepted": False, "principalEnrollmentAccepted": False,
               "catalogueRegistrationAccepted": False, "fileSigningAccepted": False, "financialEightAccepted": False}
    output = root / "artifacts/financial-composition-compile"
    output.mkdir(parents=True, exist_ok=False)
    (output / "qualification.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print('{"Stage":"compile-controls-security-and-public-receipt","Passed":true}', flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        # No exception payload, configuration, archive contents or compiler output in public logs.
        print('{"QualificationFailed":true}', flush=True)
        sys.exit(1)
