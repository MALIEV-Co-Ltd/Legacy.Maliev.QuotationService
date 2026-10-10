"""Join the 37 analytics executions to fresh compiled discovery and original TRX."""
import argparse
from collections import Counter
import json
from pathlib import Path
import xml.etree.ElementTree as ET

from check_quotation_candidate_native import NS, discovery_names, load_delayed_inventory, validate_trx, xml

ASSEMBLY = "Legacy.Maliev.QuotationService.Tests"
CLASS = ASSEMBLY + ".Analytics.QuotationAnalyticsRetryContractTests"
METHOD_COUNTS = {
    "Retry_ExactSourceInputsReachProductionStoreDespiteRetryAfter": 8,
    "Classification_PreservesExactHistoricalStatusCases": 8,
    "Processor_UsesOneDurableTransitionAtExactAttemptBoundary": 15,
    "Disabled_delivery_does_not_claim_or_send": 1,
    "Caller_cancellation_propagates_without_a_durable_outcome": 2,
    "Completed_or_exhausted_delivery_does_not_sample_retry_jitter": 3,
}
LEASE_METHOD = ASSEMBLY + ".Analytics.QuotationTerminalLoggingPipelineTests.Program_ReclaimedLease_OnlyCurrentOwnerEmitsDurableTerminalFailure"
LEASE_CASES = {
    f"{LEASE_METHOD}(staleStatus: {stale}, currentStatus: {current})"
    for stale, current in ((400, 204), (500, 204), (400, 400), (500, 500))
}


def validate_lease_focus(discovery, trx, red=False):
    names = discovery_names(discovery)
    if names != LEASE_CASES:
        raise ValueError("terminal lease discovery differs from reviewed four rows")
    if not red:
        validate_trx(trx, names, ASSEMBLY)
        return names
    root = xml(trx)
    summaries = root.findall(NS + "ResultSummary")
    if len(summaries) != 1 or summaries[0].get("outcome") != "Failed":
        raise ValueError("RED requires an original failed result summary")
    counters = summaries[0].findall(NS + "Counters")
    if len(counters) != 1 or any(counters[0].get(key) != value for key, value in
                               {"total": "4", "executed": "4", "passed": "0", "failed": "4"}.items()):
        raise ValueError("RED requires all four failed executions")
    results = root.findall("./" + NS + "Results/" + NS + "UnitTestResult")
    if len(results) != 4:
        raise ValueError("RED results incomplete")
    for result in results:
        name = result.get("testName", "")
        if name not in names or result.get("outcome") != "Failed":
            raise ValueError("RED contains an unexpected case or outcome")
        errors = result.findall("./" + NS + "Output/" + NS + "ErrorInfo")
        if len(errors) != 1:
            raise ValueError("RED assertion evidence missing")
        messages = errors[0].findall(NS + "Message")
        stacks = errors[0].findall(NS + "StackTrace")
        assertion = "Assert.Empty() Failure" if name.endswith("currentStatus: 204)") else "Assert.Single() Failure"
        if (len(messages) != 1 or not (messages[0].text or "").startswith(assertion)
                or len(stacks) != 1 or LEASE_METHOD not in (stacks[0].text or "")):
            raise ValueError("RED failure is not the reviewed stale-log assertion")
    # Join identities through the existing strict gate on an in-memory copy only.
    # Original failed TRX bytes are neither changed nor represented as successful execution.
    summaries[0].set("outcome", "Completed")
    counters[0].set("passed", "4")
    counters[0].set("failed", "0")
    for result in results:
        result.set("outcome", "Passed")
    validate_trx(ET.tostring(root), names, ASSEMBLY)
    return names


def validate_focus(discovery, trx):
    names = discovery_names(discovery)
    counts = Counter(name.split("(", 1)[0] for name in names)
    expected = {CLASS + "." + method: count for method, count in METHOD_COUNTS.items()}
    if len(names) != 37 or counts != expected:
        raise ValueError("analytics compiled discovery differs from reviewed 37-case methods")
    # All analytics rows use InlineData, with no deferred MemberData expansion.
    validate_trx(trx, names, ASSEMBLY)
    return names


def validate_coverage(focus, discovery, trx, delayed=None, cases=None):
    names = discovery_names(discovery)
    if not focus <= names:
        raise ValueError("coverage discovery omits reviewed analytics cases")
    validate_trx(trx, names, ASSEMBLY, delayed, cases)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--phase", choices=("focus", "coverage"), default="focus")
    parser.add_argument("--profile", choices=("analytics-retry", "analytics-terminal-lease-red", "analytics-terminal-lease-green"), default="analytics-retry")
    args = parser.parse_args()
    root = args.candidate.resolve(strict=True)
    directory = root / "TestResults/CandidateNative/Analytics"
    discovery = (directory / "discovery.log").read_text(encoding="utf-8")
    trx = (directory / "analytics.trx").read_bytes()
    focus = validate_focus(discovery, trx) if args.profile == "analytics-retry" else validate_lease_focus(
        discovery, trx, red=args.profile == "analytics-terminal-lease-red")
    if args.profile == "analytics-terminal-lease-red" and args.phase != "focus":
        raise ValueError("RED is a focused behavioral failure proof, not full coverage acceptance")
    if args.phase == "coverage":
        inventory = json.loads((Path(__file__).parent / "quotation-candidate-delayed-theories.json").read_text())
        delayed, cases = load_delayed_inventory(root, inventory)
        validate_coverage(focus,
                          (root / f"TestResults/CandidateNative/Suite/{ASSEMBLY}.discovery.log").read_text(encoding="utf-8"),
                          (root / "TestResults/CandidateNative/Coverage/quotation.trx").read_bytes(),
                          delayed.get(ASSEMBLY), cases.get(ASSEMBLY))
        if args.profile == "analytics-terminal-lease-green":
            names = discovery_names((root / f"TestResults/CandidateNative/Suite/{ASSEMBLY}.discovery.log").read_text(encoding="utf-8"))
            retry = Counter(name.split("(", 1)[0] for name in names if name.startswith(CLASS + "."))
            if retry != {CLASS + "." + method: count for method, count in METHOD_COUNTS.items()}:
                raise ValueError("complete coverage omits or changes the existing 37 retry cases")
    outcome = "failed with the reviewed assertions" if args.profile == "analytics-terminal-lease-red" else "passed without skips"
    print(f"Actual {args.phase} TRX matches compiled discovery; all {len(focus)} {args.profile} cases {outcome}.")


if __name__ == "__main__":
    main()
