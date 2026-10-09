"""Join the 37 analytics executions to fresh compiled discovery and original TRX."""
import argparse
from collections import Counter
import json
from pathlib import Path

from check_quotation_candidate_native import discovery_names, load_delayed_inventory, validate_trx

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
    args = parser.parse_args()
    root = args.candidate.resolve(strict=True)
    directory = root / "TestResults/CandidateNative/Analytics"
    focus = validate_focus((directory / "discovery.log").read_text(encoding="utf-8"),
                           (directory / "analytics.trx").read_bytes())
    if args.phase == "coverage":
        inventory = json.loads((Path(__file__).parent / "quotation-candidate-delayed-theories.json").read_text())
        delayed, cases = load_delayed_inventory(root, inventory)
        validate_coverage(focus,
                          (root / f"TestResults/CandidateNative/Suite/{ASSEMBLY}.discovery.log").read_text(encoding="utf-8"),
                          (root / "TestResults/CandidateNative/Coverage/quotation.trx").read_bytes(),
                          delayed.get(ASSEMBLY), cases.get(ASSEMBLY))
    print(f"Actual {args.phase} TRX matches compiled discovery; all 37 analytics cases passed without skips.")


if __name__ == "__main__":
    main()
