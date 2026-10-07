"""Observe source-owned readiness endpoints; TLS listener admission alone is insufficient."""
from datetime import datetime, timezone
import hashlib
import http.client
import json
import socket
import time

import hosted_companion_resources as h
import owned_normal_hosts as normal
import tls_listener_admission as tls
from regular_owned_files import verify_source

PREFIXES = {"Auth": "auth", "Accounting": "accounting", "Quotation": "quotation", "Order": "order",
            "IAM": "iam", "Document": "documents", "File": "file", "Notification": "emails"}


def parse_readiness(body):
    h.require(type(body) is bytes and 1 <= len(body) <= 65536, "Bounded readiness JSON required")
    def unique(pairs):
        result = {}
        for name, value in pairs:
            h.require(name not in result, "Duplicate health property")
            result[name] = value
        return result
    try:
        value = json.loads(body, object_pairs_hook=unique)
    except (ValueError, UnicodeError, RecursionError):
        raise h.AdmissionError("Actual readiness JSON rejected") from None
    h.require(type(value) is dict and set(value) == {"status", "checks", "totalDuration"}
              and value["status"] == "Healthy" and type(value["checks"]) is dict
              and 1 <= len(value["checks"]) <= 128
              and type(value["totalDuration"]) in (int, float) and 0 <= value["totalDuration"] <= 60000,
              "Actual nonempty healthy dependency report required")
    for name, check in value["checks"].items():
        h.require(type(name) is str and 1 <= len(name) <= 128 and type(check) is dict
                  and set(check) == {"status", "duration"} and check["status"] == "Healthy"
                  and type(check["duration"]) in (int, float) and 0 <= check["duration"] <= 60000,
                  "All actual reported dependencies must be healthy")
    return {"checkCount": len(value["checks"]), "bodySha256": hashlib.sha256(body).hexdigest()}


class DeadlineHTTPSConnection(h.DeadlineHTTPConnection):
    def __init__(self, spec, deadline, client):
        super().__init__(spec.host_ip, spec.host_port, deadline)
        self.spec = spec
        self.client = client

    def connect(self):
        h.require(self.host in ("127.0.0.1", "::1") and self._tunnel_host is None, "Direct owned readiness only")
        raw = socket.socket(socket.AF_INET if self.host == "127.0.0.1" else socket.AF_INET6, socket.SOCK_STREAM)
        wrapped = None
        try:
            raw.settimeout(h.remaining_time(self.deadline))
            raw.connect((self.host, self.port))
            raw.settimeout(h.remaining_time(self.deadline))
            wrapped = self.client.wrap_socket(raw, server_hostname=self.host)
            h.require(hashlib.sha256(wrapped.getpeercert(binary_form=True)).hexdigest()
                      == self.spec.tls.served_der_sha256, "Actual readiness certificate differs")
            h.remaining_time(self.deadline)
            self.sock = h.DeadlineSocket(wrapped, self.deadline)
        except BaseException:
            if wrapped is not None:
                wrapped.close()
            else:
                raw.close()
            raise


def observe_health(spec, process, ticks, dotnet, context, environment, budget=5):
    context.validate(environment, datetime.now(timezone.utc))
    h.require(spec.owner in PREFIXES and 0 < budget <= 5, "Known owner and bounded readiness budget required")
    deadline = time.monotonic() + min(budget, (h.instant(context.expires_utc) - datetime.now(timezone.utc)).total_seconds())
    h.remaining_time(deadline)
    verify_source(spec, deadline)
    before = tls.observe_listener(spec, process, ticks, dotnet, context, environment)
    h.remaining_time(deadline)
    client = None if spec.owner == "File" else tls.load_identity(spec.tls)
    h.remaining_time(deadline)
    connection = h.deadline_http_connection(spec.host_ip, spec.host_port, deadline) if client is None else DeadlineHTTPSConnection(spec, deadline, client)
    response = None
    try:
        route = "/" + PREFIXES[spec.owner] + "/readiness"
        connection.request("GET", route, headers={"Accept": "application/json", "Connection": "close"})
        response = connection.getresponse()
        h.require(response.status == 200 and response.getheader("Content-Encoding") is None
                  and response.getheader("Content-Type") in ("application/json", "application/json; charset=utf-8"),
                  "Actual readiness rejected, redirected or encoded")
        result = parse_readiness(response.read(65537))
        h.remaining_time(deadline)
    except (OSError, http.client.HTTPException, json.JSONDecodeError):
        raise h.AdmissionError("Actual bounded application readiness failed") from None
    finally:
        try:
            if response is not None:
                response.close()
        finally:
            connection.close()
    if client is not None:
        tls.load_identity(spec.tls)
    h.remaining_time(deadline)
    after = tls.observe_listener(spec, process, ticks, dotnet, context, environment)
    h.remaining_time(deadline)
    h.require(before == after, "Owner/listener changed during application health")
    return {"owner": spec.owner, "route": route, "listenerInode": after, "applicationHealthObserved": True,
            "observedUtc": datetime.now(timezone.utc).isoformat(), "genuineEightHostFinancialAccepted": False, **result}
