"""Private, pure receipt/phase admission. No host launch, enrollment or acceptance emitter."""
from dataclasses import dataclass, field
import hashlib
import json
import math
import re


class Rejected(ValueError):
    """Messages intentionally omit private input values."""


def require(condition, reason):
    if not condition:
        raise Rejected(reason)


def _pairs(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON field")
        result[key] = value
    return result


def _finite_float(text):
    value = float(text)
    require(math.isfinite(value), "Nonfinite JSON numeric exponent")
    return value


def finite_timestamp(value):
    # Avoid converting unbounded Python integers through math.isfinite(float).
    if type(value) is int:
        return abs(value) <= 2 ** 53 - 1
    return type(value) is float and math.isfinite(value) and abs(value) <= 2 ** 53 - 1


def parse_private(raw, limit=1048576):
    require(type(raw) is bytes and 0 < len(raw) <= limit, "Bounded private bytes required")
    try:
        return json.loads(raw.decode("utf-8"), object_pairs_hook=_pairs, parse_float=_finite_float,
                          parse_constant=lambda _: (_ for _ in ()).throw(Rejected("Nonfinite JSON")))
    except Rejected:
        raise
    except (UnicodeError, ValueError, RecursionError, OverflowError):
        raise Rejected("Invalid private JSON") from None


def exact(value, keys):
    require(type(value) is dict and set(value) == set(keys), "Exact receipt fields required")


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def hex_value(value, length):
    return type(value) is str and re.fullmatch("[0-9a-f]{%d}" % length, value) is not None


@dataclass(frozen=True, repr=False)
class SourceAssociation:
    # Parent supplies independently observed metadata, NOT fields claimed emitted by V6.
    owner: str
    head: str
    tree: str
    build_sha256: str
    run: str
    attempt: str
    artifact_sha256: str

    def validate(self):
        require(type(self.owner) is str and bool(self.owner), "Owner required")
        require(hex_value(self.head, 40) and hex_value(self.tree, 40), "Exact source association required")
        require(hex_value(self.build_sha256, 64) and hex_value(self.artifact_sha256, 64), "Exact byte association required")
        require(type(self.run) is str and self.run.isascii() and self.run.isdecimal()
                and int(self.run) > 0 and type(self.attempt) is str and self.attempt.isascii()
                and self.attempt.isdecimal() and int(self.attempt) > 0, "Exact hosted context required")


@dataclass(frozen=True, repr=False)
class PrivateReceipt:
    association: SourceAssociation
    raw: bytes = field(repr=False)

    @classmethod
    def admit(cls, raw, observed, expected):
        require(type(observed) is SourceAssociation and type(expected) is SourceAssociation,
                "Independent expected and observed associations required")
        observed.validate(); expected.validate()
        require(observed == expected, "Owner/source/run association differs")
        require(type(raw) is bytes and digest(raw) == expected.artifact_sha256, "Private byte digest differs")
        parse_private(raw)
        return cls(expected, raw)

    def read(self):
        # Recheck held immutable bytes on every use. Never emit their contents.
        self.association.validate()
        require(digest(self.raw) == self.association.artifact_sha256, "Private receipt changed")
        return parse_private(self.raw)


class PrivateOwnerReceipts:
    def __init__(self):
        self._receipts = {}

    def add(self, capability, receipt):
        require(type(capability) is str and bool(capability) and type(receipt) is PrivateReceipt,
                "Typed receipt capability required")
        key = (receipt.association.owner, capability)
        require(key not in self._receipts, "Repeated owner capability; no repeated enrollment")
        value = receipt.read()
        if type(value) is dict:
            standalone = None
            if "hostedCompileAccepted" in value or ("compiled" in value and "compileControls" in value):
                standalone = "adapter-compile-controls"
            elif "nativeCodecAccepted" in value:
                standalone = "synthetic-codec-controls"
            elif "requiredRawSuiteAccepted" in value:
                standalone = "counter-required-raw-suite"
            require(standalone is None or capability == standalone,
                    "Standalone receipt cannot be relabeled as owning runtime/business evidence")
        self._receipts[key] = receipt

    def require_capabilities(self, requirements):
        require(type(requirements) is tuple and len(requirements) > 0, "Explicit capability requirements required")
        require(len(set(requirements)) == len(requirements), "Duplicate capability requirement")
        require(all(key in self._receipts for key in requirements), "Missing actual owner capability")
        selected = [self._receipts[key] for key in requirements]
        require(len({(r.association.run, r.association.attempt) for r in selected}) == 1,
                "Cross-run aggregation prohibited")
        for owner in {r.association.owner for r in selected}:
            require(len({(r.association.head, r.association.tree, r.association.build_sha256)
                         for r in selected if r.association.owner == owner}) == 1,
                    "Conflicting owner source graphs prohibited")
        return tuple(selected)  # Internal only. Does not certify semantics or financial acceptance.



def require_current_source_graph(store, expected_sources, catalogue):
    """Prerequisite associations for CURRENT Accounting668f caller graph, not eight acceptance.

    Country is served through Catalog's /countries/{id}; no separate Country host inferred.
    Owner replacement snapshot graph must get a distinct source-owned contract, not omit extras here.
    """
    owners = ("Auth", "IAM", "Accounting", "Quotation", "Order", "File", "Document", "Notification",
              "Customer", "Employee", "Catalog")
    require(type(store) is PrivateOwnerReceipts and type(expected_sources) is dict
            and set(expected_sources) == set(owners), "Unknown/missing qualified current producer graph")
    accounting = expected_sources["Accounting"]
    require(type(accounting) is SourceAssociation
            and accounting.head == "668f2cb63c64b911db776329b983dd91944b3b8c",
            "Replacement Accounting caller graph not yet source-qualified here")
    require(type(catalogue) is tuple
            and "legacy.accounting.invoice-financial-ownership.read" in catalogue,
            "Current Accounting source permission cannot be substituted by a pending alias")
    require_three_segment_catalogue(catalogue)  # Current literal fails; pending owner correction is not fabricated.
    receipts = store.require_capabilities(tuple((owner, "source-graph") for owner in owners))
    for receipt in receipts:
        expected = expected_sources[receipt.association.owner]
        require(type(expected) is SourceAssociation, "Independent qualified owning source required")
        expected.validate()
        require(receipt.association == expected, "Producer exact graph association differs")
    # This validates source/byte associations only. No READY, principal or policy assertions synthesized.


def observe_migration_tables(receipt, expected_contracts):
    # contracts must arrive from separately qualified sealed original source, per owning process.
    rows = receipt.read()
    require(type(rows) is list and len(rows) == len(expected_contracts) > 0,
            "Exact owning observation inventory required")
    seen = set()
    for row in rows:
        exact(row, ("Role", "ContextType", "MigrationDigest", "MigrationCount", "MappedTableCount"))
        role = row["Role"]
        require(type(role) is str and role in expected_contracts and role not in seen, "Unknown/repeated schema role")
        contract = expected_contracts[role]
        require(contract["owner"] == receipt.association.owner and contract["commit"] == receipt.association.head,
                "Owning schema source association differs")
        migrations = contract["migrationIds"]
        require(type(migrations) is list and migrations and all(type(m) is str and m for m in migrations)
                and len(set(migrations)) == len(migrations), "Exact source migrations required")
        require(row["ContextType"] == contract["contextFullName"], "Owning runtime context differs")
        require(type(row["MigrationCount"]) is int and row["MigrationCount"] == len(migrations)
                and row["MigrationDigest"] == digest(("\n".join(migrations) + "\n").encode()),
                "Applied source migration observation differs")
        require(type(row["MappedTableCount"]) is int and 0 < row["MappedTableCount"] <= 2 ** 31 - 1, "Physical mapped tables absent")
        seen.add(role)
    # Do not return full schema accepted. Columns/constraints/owned DB identity remain external obligations.
    return frozenset(seen)


def require_three_segment_catalogue(permissions):
    require(type(permissions) is tuple and permissions, "Source permission inventory required")
    require(len(set(permissions)) == len(permissions), "Duplicate source permission")
    require(all(type(p) is str and re.fullmatch(r"[a-z0-9-]+\.[a-z0-9-]+\.[a-z0-9-]+", p) for p in permissions),
            "Coherent three-segment source catalogue required")
    # Preliminary representation check only. Original IAM validator remains authoritative.


def quarantine_transition(method):
    def guarded(self, *args, **kwargs):
        require(not self._failed, "Coordinator quarantined after failed transition")
        try:
            return method(self, *args, **kwargs)
        except Exception:
            self._failed = True
            raise Rejected("Invalid transition; coordinator quarantined") from None
    return guarded


class PhaseCoordinator:
    """One reserved pipe pair; parent owns transport/identity/timeout/cleanup proof.

    This pure state machine has no I/O. Returning bytes authorizes no business activity.
    It only supplies the next protocol write after a parsed actual-message barrier.
    """
    phases = ("baseline", "completion", "replay", "stop")

    def __init__(self, now, expires):
        require(finite_timestamp(now) and finite_timestamp(expires) and now < expires,
                "Finite monotonic lease required")
        self._failed = False
        self._expires = expires
        self._last_now = now
        self._state = "start"
        self._index = 0
        self._pending_until = None

    def _time(self, now):
        require(finite_timestamp(now) and self._last_now <= now < self._expires, "Expired/regressed monotonic lease")
        self._last_now = now
        if self._pending_until is not None:
            require(now < self._pending_until, "Phase barrier exceeded finite ceiling")

    @staticmethod
    def _line(raw):
        require(type(raw) is bytes and 0 < len(raw) <= 4096 and raw.endswith(b"\n")
                and raw.count(b"\n") == 1 and b"\r" not in raw, "Exact single LF message required")
        return raw[:-1]

    @quarantine_transition
    def collector_started(self, raw, now):
        self._time(now)
        require(self._state == "start", "Repeated/unordered sessions start")
        message = parse_private(self._line(raw), 4096)
        exact(message, ("SessionsStarted", "HttpObservationsComplete"))
        require(message["SessionsStarted"] is True and message["HttpObservationsComplete"] is False,
                "Actual sessions start observation required")
        self._state = "producer"

    @quarantine_transition
    def producer_phase(self, raw, now):
        self._time(now)
        require(self._state == "producer", "Concurrent/unordered producer phase")
        command = self._line(raw)
        expected = self.phases[self._index].encode("ascii")
        require(command == expected, "Exact ordered producer phase required")
        self._state = "stop" if command == b"stop" else "barrier"
        self._pending_until = min(self._expires, now + 15)
        return command + b"\n"  # relay only, never a producer acknowledgement

    @quarantine_transition
    def collector_held(self, raw, now):
        self._time(now)
        require(self._state == "barrier", "No pending collector barrier")
        message = parse_private(self._line(raw), 4096)
        exact(message, ("Phase", "BoundaryHeld", "HttpObservationsComplete"))
        phase = self.phases[self._index]
        require(message["Phase"] == phase and message["BoundaryHeld"] is True
                and message["HttpObservationsComplete"] is False, "Exact held collector boundary required")
        self._index += 1; self._state = "producer"; self._pending_until = None
        return ("held " + phase + "\n").encode("ascii")

    @quarantine_transition
    def collector_stopped(self, receipt, exit_code, reader_eof, now, expected_input_sha256, expected_targets):
        self._time(now)
        require(self._state == "stop", "Stop requires actual replay barrier")
        require(type(receipt) is PrivateReceipt and type(exit_code) is int and exit_code == 0
                and reader_eof is True, "Actual child exit/readers and associated stop receipt required")
        value = receipt.read()
        exact(value, ("SessionCleanupVerified", "Observation"))
        require(value["SessionCleanupVerified"] is True, "Actual collector cleanup required")
        observation = value["Observation"]
        exact(observation, ("HttpObservationsComplete", "GenuineEightHostFinancialAccepted", "SmtpNoSendProven",
                            "SocketNoSendProven", "IncomingMethodSchemaCanariesObserved", "Scope", "PrivateInputSha256", "Observations"))
        require(observation["HttpObservationsComplete"] is True
                and observation["IncomingMethodSchemaCanariesObserved"] is True
                and observation["GenuineEightHostFinancialAccepted"] is False
                and observation["SmtpNoSendProven"] is False and observation["SocketNoSendProven"] is False,
                "Collector scope or actual completion differs")
        require(hex_value(observation["PrivateInputSha256"], 64)
                and type(observation["Observations"]) is dict and len(observation["Observations"]) == 4,
                "Four selected actual observation records required")
        require(observation["Scope"] == "HttpClient/ASP.NET observations from attach through drained stop; no pre-attach coverage",
                "Exact collector observation scope required")
        require(hex_value(expected_input_sha256, 64)
                and observation["PrivateInputSha256"] == expected_input_sha256, "Actual private collector input differs")
        labels = {
            "Accounting": ("AccountingInvoiceRenderPost", "AccountingReceiptRenderPost", "AccountingFileUploadPost", "AccountingOtherUploadBoundaryMethod"),
            "Document": ("DocumentInvoiceRenderBoundary", "DocumentReceiptRenderBoundary"),
            "File": ("FileUploadBoundary",), "Notification": ("NotificationProviderHttpAttempt",),
        }
        require(type(expected_targets) is dict and set(expected_targets) == set(labels)
                and set(observation["Observations"]) == set(labels), "Exact independent selected targets required")
        identity = ("Pid", "KernelStartTicks", "NativeStartUtcTicks", "SourceSha", "SourceTree", "ExecutableSha256", "DllSha256")
        windows = ("BeforeBaseline", "Completion", "Replay", "AfterReplayThroughDrain")
        pids = set()
        for owner, target in observation["Observations"].items():
            exact(target, identity + windows + ("EventsLost", "StreamEofObserved"))
            expected = expected_targets[owner]
            exact(expected, identity)
            for name in identity:
                require(type(target[name]) is type(expected[name]) and target[name] == expected[name],
                        "Observed target source/process differs")
            require(all(type(target[n]) is int and 0 < target[n] <= maximum
                        for n, maximum in (("Pid", 2 ** 31 - 1), ("KernelStartTicks", 2 ** 64 - 1),
                                           ("NativeStartUtcTicks", 2 ** 63 - 1)))
                    and hex_value(target["SourceSha"], 40) and hex_value(target["SourceTree"], 40)
                    and hex_value(target["ExecutableSha256"], 64) and hex_value(target["DllSha256"], 64),
                    "Exact source/process handles required")
            require(target["Pid"] not in pids, "Repeated target process")
            pids.add(target["Pid"])
            require(type(target["EventsLost"]) is int and target["EventsLost"] == 0
                    and target["StreamEofObserved"] is True, "Lossless actual drained stream required")
            for window in windows:
                exact(target[window], labels[owner])
                for counts in target[window].values():
                    exact(counts, ("Started", "Completed", "Successful"))
                    require(all(type(v) is int and 0 <= v <= 2 ** 31 - 1 for v in counts.values())
                            and counts["Started"] == counts["Completed"]
                            and counts["Successful"] <= counts["Completed"], "Actual balanced window counts required")
        # Counts observed, not asserted as expected business effects or no-send proof.
        self._state = "done"; self._pending_until = None
        return b"stopped\n"
