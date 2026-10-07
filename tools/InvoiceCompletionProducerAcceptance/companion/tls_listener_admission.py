"""Source proposal: task-local trust and held-child listener/TLS admission.

TLS readiness is a verified handshake, not application health or financial acceptance.
No import side effects, global trust modification, or verification bypass.
"""
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import os
from pathlib import Path
import socket
import ssl
import stat
import time

import hosted_companion_resources as h


@dataclass(frozen=True)
class TlsIdentity:
    task_root: str
    ca_path: str
    ca_sha256: str
    certificate_path: str
    certificate_sha256: str
    private_key_path: str
    served_der_sha256: str
    trust_directory: str


def endpoint(spec):
    host = spec.host_ip if spec.host_ip == "127.0.0.1" else "[::1]"
    return f"{'http' if spec.owner == 'File' else 'https'}://{host}:{spec.host_port}"


def validate_configuration(spec):
    h.consumed(spec.environment, "ASPNETCORE_URLS", endpoint(spec))
    # Alternative endpoint definitions can override URLs and bypass the intended binding.
    forbidden = ("kestrel__endpoints", "aspnetcore_http_ports", "aspnetcore_https_ports",
                 "dotnet_http_ports", "dotnet_https_ports", "urls", "aspnetcore__urls", "dotnet_urls",
                 "aspnetcore_kestrel", "dotnet_kestrel")
    for key in spec.environment:
        normalized = key.replace(":", "__").lower()
        h.require(not any(normalized == prefix or normalized.startswith(prefix + "__")
                          for prefix in forbidden), "Conflicting endpoint configuration")
        h.require(normalized not in ("pythonhttpsverify", "dotnet_system_net_http_usessocketshttphandler"),
                  "Unexpected TLS bypass configuration")
    if spec.owner == "File":
        h.require(spec.tls is None, "Only admitted File HTTP profile may omit TLS")
        h.require(not any(key.replace(":", "__").lower().startswith("kestrel__certificates")
                          for key in spec.environment), "File HTTP profile has unexpected certificate configuration")
        return
    h.require(isinstance(spec.tls, TlsIdentity), "Seven Production hosts require explicit task TLS identity")
    identity = spec.tls
    for digest in (identity.ca_sha256, identity.certificate_sha256, identity.served_der_sha256):
        h.require(h.HEX64.fullmatch(digest) is not None, "Explicit public TLS hashes required")
    h.consumed(spec.environment, "Kestrel__Certificates__Default__Path", identity.certificate_path)
    h.consumed(spec.environment, "Kestrel__Certificates__Default__KeyPath", identity.private_key_path)
    h.consumed(spec.environment, "SSL_CERT_FILE", identity.ca_path)
    h.consumed(spec.environment, "SSL_CERT_DIR", identity.trust_directory)
    allowed = {"kestrel__certificates__default__path", "kestrel__certificates__default__keypath"}
    h.require(all(key.replace(":", "__").lower() in allowed for key in spec.environment
                  if key.replace(":", "__").lower().startswith("kestrel__certificates")),
              "Unexpected alternative certificate configuration")


def owned_path(root, value, directory=False, private=False):
    path = Path(value)
    h.require(path.is_absolute() and path == path.resolve(strict=True)
              and path.is_relative_to(root) and path != root,
              "TLS material must belong to explicit task root")
    for ancestor in (path, *path.parents):
        h.require(not ancestor.is_symlink(), "TLS path must not traverse symbolic links")
        if ancestor == root:
            break
    info = path.stat()
    h.require(info.st_uid == os.getuid() and (stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)),
              "TLS material ownership/type differs")
    if private:
        h.require(info.st_mode & 0o077 == 0, "Private TLS material permissions are too broad")
    return path


def load_identity(identity):
    root = Path(identity.task_root)
    h.require(root.is_absolute() and not root.is_symlink() and root.resolve() == root,
              "Explicit canonical task-owned TLS root required")
    info = root.stat()
    h.require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid() and info.st_mode & 0o077 == 0,
              "TLS task root must be private and owned by current task user")
    ca = owned_path(root, identity.ca_path)
    cert = owned_path(root, identity.certificate_path)
    key = owned_path(root, identity.private_key_path, private=True)
    trust = owned_path(root, identity.trust_directory, directory=True)
    # An empty isolated CA directory prevents system directory fallback; CA file is explicit.
    h.require(next(trust.iterdir(), None) is None, "Task trust directory must be empty")
    ca_bytes = h.bounded_file(ca, 65536)
    cert_bytes = h.bounded_file(cert, 65536)
    h.bounded_file(key, 65536)  # Bound key input without recording or hashing private material.
    h.require(hashlib.sha256(ca_bytes).hexdigest() == identity.ca_sha256
              and hashlib.sha256(cert_bytes).hexdigest() == identity.certificate_sha256,
              "Public certificate/CA file hash differs")
    pem = cert_bytes.decode("ascii")
    h.require(pem.count("-----BEGIN CERTIFICATE-----") == 1, "Exactly one leaf certificate required")
    der = ssl.PEM_cert_to_DER_cert(pem)
    h.require(hashlib.sha256(der).hexdigest() == identity.served_der_sha256,
              "Declared leaf DER hash differs from certificate")
    server = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    server.load_cert_chain(str(cert), str(key))  # Validate key pairing; starts no server.
    client = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    client.load_verify_locations(cafile=str(ca))  # No system/default trust store loading.
    h.require(client.verify_mode == ssl.CERT_REQUIRED and client.check_hostname,
              "Normal TLS certificate and hostname verification required")
    h.require(h.bounded_file(ca, 65536) == ca_bytes and h.bounded_file(cert, 65536) == cert_bytes,
              "Public TLS material changed during context loading")
    return client


def require_listener_identity(start_ticks, expected_ticks, inode, fd_links):
    h.require(start_ticks == expected_ticks, "Held child process generation changed")
    h.require(f"socket:[{inode}]" in fd_links, "Listener belongs to a foreign process")
    return inode


def require_only_owned_listener(tables, fd_links, expected_inode):
    owned = {link[len("socket:["):-1] for link in fd_links if link.startswith("socket:[") and link.endswith("]")}
    listeners = []
    for table in tables:
        lines = table.splitlines()
        h.require(lines and "local_address" in lines[0], "Kernel listener table unavailable")
        for line in lines[1:]:
            fields = line.split()
            h.require(len(fields) >= 10, "Kernel listener row incomplete")
            if fields[3] == "0A" and fields[9] in owned:
                listeners.append(fields[9])
    h.require(listeners == [str(expected_inode)], "Normal child must own exactly its declared listener")


def observe_listener(spec, process, expected_ticks, dotnet, context, launcher_environment):
    context.validate(launcher_environment, datetime.now(timezone.utc))
    h.require(process.poll() is None, "Held normal child exited before listener admission")
    prefix = f"/proc/{process.pid}"
    before = h.process_start_ticks(h.bounded_file(prefix + "/stat", 4096), process.pid)
    h.require(os.readlink(prefix + "/exe") == dotnet, "Held normal executable changed")
    args = h.bounded_file(prefix + "/cmdline", 65536).split(b"\0")
    h.require(args == [dotnet.encode(), spec.executable_dll.encode(), b""], "Normal host command line differs")
    actual = h.process_environment(h.bounded_file(prefix + "/environ", 262144))
    for name, value in spec.environment.items():
        h.consumed(actual, name, value)
    validate_configuration(type("ObservedSpec", (), {**spec.__dict__, "environment": actual})())
    for name, value in {"GITHUB_ACTIONS": "true", "GITHUB_RUN_ID": context.run_id,
                        "GITHUB_RUN_ATTEMPT": str(context.attempt), "C821_FIXTURE_RUN_ID": context.lease_id,
                        "C821_FIXTURE_EXPIRES_UTC": context.expires_utc,
                        "DOTNET_GCHeapHardLimit": format(spec.heap_limit_bytes, "x")}.items():
        h.consumed(actual, name, value)
    tables = [h.bounded_file(path, 2 * 1024**2).decode("ascii") for path in ("/proc/net/tcp", "/proc/net/tcp6")]
    inode = h.listening_inode(tables, spec.host_ip, spec.host_port)
    links = []
    for index, entry in enumerate(Path(prefix + "/fd").iterdir()):
        h.require(index < 4096, "Held child FD observation exceeds bound")
        try:
            links.append(os.readlink(entry))
        except FileNotFoundError:
            continue
    require_listener_identity(before, expected_ticks, inode, links)
    require_only_owned_listener(tables, links, inode)
    after = h.process_start_ticks(h.bounded_file(prefix + "/stat", 4096), process.pid)
    h.require(after == expected_ticks and process.poll() is None, "Child generation changed during listener observation")
    context.validate(launcher_environment, datetime.now(timezone.utc))
    return inode


def verify_peer(client, spec, timeout):
    h.require(client.verify_mode == ssl.CERT_REQUIRED and client.check_hostname,
              "TLS verification bypass rejected")
    h.require(0 < timeout <= 5, "Finite TLS observation budget required")
    deadline = time.monotonic() + timeout
    with socket.create_connection((spec.host_ip, spec.host_port), timeout=timeout) as raw:
        remaining = deadline - time.monotonic()
        h.require(remaining > 0, "TLS connect deadline expired")
        raw.settimeout(remaining)
        # Literal IP SAN validation; never a synthetic hostname or overridden verifier.
        with client.wrap_socket(raw, server_hostname=spec.host_ip) as stream:
            h.require(time.monotonic() <= deadline, "TLS handshake deadline expired")
            peer = stream.getpeercert(binary_form=True)
            h.require(peer and len(peer) <= 65536, "Bounded served public certificate required")
            digest = hashlib.sha256(peer).hexdigest()
            h.require(digest == spec.tls.served_der_sha256, "Actual served certificate DER differs")
            return digest


def admit_readiness(spec, process, ticks, dotnet, context, environment, budget=5):
    validate_configuration(spec)
    remaining = (h.instant(context.expires_utc) - datetime.now(timezone.utc)).total_seconds()
    h.require(0 < budget <= 5 and remaining > 0, "Readiness observation lease/budget expired")
    client = None if spec.owner == "File" else load_identity(spec.tls)
    before = observe_listener(spec, process, ticks, dotnet, context, environment)
    digest = None if client is None else verify_peer(client, spec, min(budget, remaining))
    if client is not None:
        load_identity(spec.tls)  # Re-observe public pins after handshake; retain no private material.
    after = observe_listener(spec, process, ticks, dotnet, context, environment)
    h.require(after == before, "Listener inode changed during TLS observation")
    receipt = {"listenerInode": before, "transport": "http" if client is None else "https",
               "listenerReadinessObserved": True, "applicationHealthObserved": False}
    if client is not None:
        receipt.update({"publicCaSha256": spec.tls.ca_sha256,
                        "publicCertificateSha256": spec.tls.certificate_sha256,
                        "servedCertificateDerSha256": digest, "normalTlsValidation": True})
    return receipt
