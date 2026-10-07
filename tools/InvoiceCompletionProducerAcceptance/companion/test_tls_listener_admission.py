"""Pure mocked controls only: no certificate files, sockets, hosts, or processes created."""
from dataclasses import replace
import hashlib
from pathlib import Path
import tempfile
from datetime import datetime, timezone
import ssl
import unittest
from unittest.mock import MagicMock, patch

import hosted_companion_resources as h
import tls_listener_admission as t
from test_owned_normal_hosts import specs
from test_hosted_companion_resources import CTX, ENV


class TlsAdmissionControls(unittest.TestCase):
    def normal(self):
        return next(spec for spec in specs() if spec.owner == "Quotation")

    def test_seven_https_and_only_file_http(self):
        for spec in specs():
            t.validate_configuration(spec)
            self.assertEqual(t.endpoint(spec).startswith("https:"), spec.owner != "File")

    def test_http_production_host_rejected(self):
        spec=self.normal(); env=dict(spec.environment); env["ASPNETCORE_URLS"]=t.endpoint(spec).replace("https:","http:")
        with self.assertRaises(h.AdmissionError): t.validate_configuration(replace(spec,environment=env))

    def test_missing_identity_and_hash_rejected(self):
        for identity in (None,replace(self.normal().tls,ca_sha256="")):
            with self.subTest(identity=identity):
                with self.assertRaises(h.AdmissionError): t.validate_configuration(replace(self.normal(),tls=identity))

    def test_file_tls_identity_rejected(self):
        spec=next(spec for spec in specs() if spec.owner == "File")
        with self.assertRaises(h.AdmissionError): t.validate_configuration(replace(spec,tls=self.normal().tls))

    def test_overrides_aliases_and_bypass_rejected(self):
        for key,value in (("Kestrel__Endpoints__Other__Url","http://127.0.0.1:9"),
                          ("ASPNETCORE_HTTP_PORTS","45000"),("ASPNETCORE:URLS",t.endpoint(self.normal())),
                          ("Kestrel__Certificates__Default__Password","unused"),("PYTHONHTTPSVERIFY","0")):
            env=dict(self.normal().environment); env[key]=value
            with self.subTest(key=key):
                with self.assertRaises(h.AdmissionError): t.validate_configuration(replace(self.normal(),environment=env))

    def test_foreign_listener_and_pid_generation_rejected(self):
        for ticks,links in ((123,["socket:[999]"]),(999,["socket:[45]"])):
            with self.subTest(ticks=ticks):
                with self.assertRaises(h.AdmissionError): t.require_listener_identity(ticks,123,45,links)
        self.assertEqual(t.require_listener_identity(123,123,45,["socket:[45]"]),45)

    def test_traversal_cannot_escape_task_certificate_root(self):
        with tempfile.TemporaryDirectory() as temporary:
            base=Path(temporary).resolve(); root=base/"tls"; outside=base/"outside"
            root.mkdir(); outside.mkdir(); certificate=outside/"certificate.pem"; certificate.write_text("controlled public fixture")
            for value in (root/".."/"outside"/"certificate.pem",certificate):
                with self.subTest(value=value):
                    with self.assertRaises(h.AdmissionError): t.owned_path(root,str(value))

    def test_additional_owned_listener_fails_without_adopting_foreign_sockets(self):
        header="sl local_address rem_address st tx rx tr time retr uid inode"
        one="0: 0100007F:AF00 00000000:0000 0A 0 0 0 0 0 45"
        two="1: 00000000:AF01 00000000:0000 0A 0 0 0 0 0 99"
        tables=[header+"\n"+one+"\n"+two,header]
        t.require_only_owned_listener(tables,["socket:[45]"],45)
        with self.assertRaises(h.AdmissionError): t.require_only_owned_listener(tables,["socket:[45]","socket:[99]"],45)

    def test_verification_bypass_rejected_before_connect(self):
        for mode,hostname in ((ssl.CERT_NONE,True),(ssl.CERT_REQUIRED,False)):
            context=MagicMock(); context.verify_mode=mode; context.check_hostname=hostname
            with patch.object(t.socket,"create_connection") as connect:
                with self.assertRaises(h.AdmissionError): t.verify_peer(context,self.normal(),1)
                connect.assert_not_called()

    def peer(self,der):
        context=MagicMock(); context.verify_mode=ssl.CERT_REQUIRED; context.check_hostname=True
        context.wrap_socket.return_value.__enter__.return_value.getpeercert.return_value=der
        return context

    def test_actual_served_der_mismatch_rejected(self):
        with patch.object(t.socket,"create_connection"):
            with self.assertRaises(h.AdmissionError): t.verify_peer(self.peer(b"wrong certificate"),self.normal(),1)

    def test_verified_served_der_and_literal_ip_san(self):
        der=b"mocked public certificate"; spec=self.normal()
        spec=replace(spec,tls=replace(spec.tls,served_der_sha256=hashlib.sha256(der).hexdigest()))
        context=self.peer(der)
        with patch.object(t.socket,"create_connection") as connect:
            self.assertEqual(t.verify_peer(context,spec,1),spec.tls.served_der_sha256)
            connect.assert_called_once_with((spec.host_ip,spec.host_port),timeout=1)
            self.assertEqual(context.wrap_socket.call_args.kwargs["server_hostname"],spec.host_ip)

    def test_tls_certificate_validation_failure_propagates(self):
        context=self.peer(b"unused"); context.wrap_socket.side_effect=ssl.SSLCertVerificationError("untrusted")
        with patch.object(t.socket,"create_connection"):
            with self.assertRaises(ssl.SSLCertVerificationError): t.verify_peer(context,self.normal(),1)

    def test_timeout_and_missing_certificate_fail_closed(self):
        with patch.object(t.socket,"create_connection",side_effect=TimeoutError("bounded")):
            with self.assertRaises(TimeoutError): t.verify_peer(self.peer(b"unused"),self.normal(),1)
        root=MagicMock(); root.is_absolute.return_value=True; root.is_symlink.return_value=False
        root.resolve.return_value=root; root.stat.side_effect=FileNotFoundError("missing task certificate")
        with patch.object(t,"Path",return_value=root):
            with self.assertRaises(FileNotFoundError): t.load_identity(self.normal().tls)

    def test_readiness_reobserves_same_owned_inode(self):
        with patch.object(t,"datetime") as clock,patch.object(t,"load_identity",return_value=self.peer(b"unused")), \
             patch.object(t,"observe_listener",side_effect=[45,999]) as observation, \
             patch.object(t,"verify_peer",return_value="d"*64):
            clock.now.return_value=h.instant(CTX.issued_utc)
            with self.assertRaises(h.AdmissionError): t.admit_readiness(self.normal(),MagicMock(),123,"/sdk/dotnet",CTX,ENV)
            self.assertEqual(observation.call_count,2)

    def test_file_readiness_observes_listener_without_tls_bypass(self):
        spec=next(spec for spec in specs() if spec.owner == "File")
        with patch.object(t,"datetime") as clock,patch.object(t,"load_identity") as load,patch.object(t,"verify_peer") as peer, \
             patch.object(t,"observe_listener",return_value=45) as observe:
            clock.now.return_value=h.instant(CTX.issued_utc)
            receipt=t.admit_readiness(spec,MagicMock(),123,"/sdk/dotnet",CTX,ENV)
            load.assert_not_called(); peer.assert_not_called(); self.assertEqual(observe.call_count,2)
            self.assertFalse(receipt["applicationHealthObserved"]); self.assertEqual(receipt["transport"],"http")

    def test_listener_observer_checks_held_generation_not_receipt_only(self):
        process=MagicMock(); process.pid=123; process.poll.return_value=None
        context=MagicMock(); context.run_id=CTX.run_id; context.attempt=CTX.attempt
        context.lease_id=CTX.lease_id; context.expires_utc=CTX.expires_utc
        spec=self.normal()
        actual=dict(spec.environment)
        actual.update({"GITHUB_ACTIONS":"true","GITHUB_RUN_ID":CTX.run_id,"GITHUB_RUN_ATTEMPT":str(CTX.attempt),
                       "C821_FIXTURE_RUN_ID":CTX.lease_id,"C821_FIXTURE_EXPIRES_UTC":CTX.expires_utc,
                       "DOTNET_GCHeapHardLimit":format(spec.heap_limit_bytes,"x")})
        def read(path,maximum):
            if path.endswith("cmdline"): return b"/sdk/dotnet\0"+spec.executable_dll.encode()+b"\0"
            if path.endswith("/tcp"): return b"sl local_address rem_address st tx rx tr time retr uid inode\n0: 0100007F:AF00 00000000:0000 0A 0 0 0 0 0 45"
            if path.endswith("/tcp6"): return b"sl local_address rem_address st tx rx tr time retr uid inode"
            return b"observed"
        def link(path):
            return "/sdk/dotnet" if str(path).endswith("/exe") else "socket:[45]"
        for generations,links in (([123,123],["/mock/fd"]),([123,999],["/mock/fd"]),([123,123],[])):
            with self.subTest(generations=generations,links=links), \
                 patch.object(h,"bounded_file",side_effect=read), \
                 patch.object(h,"process_start_ticks",side_effect=generations), \
                 patch.object(h,"process_environment",return_value=actual), \
                 patch.object(h,"listening_inode",return_value=45), \
                 patch.object(t.os,"readlink",side_effect=link),patch.object(t.Path,"iterdir",return_value=iter(links)):
                if generations[-1] == 999 or not links:
                    with self.assertRaises(h.AdmissionError): t.observe_listener(spec,process,123,"/sdk/dotnet",context,ENV)
                else:
                    self.assertEqual(t.observe_listener(spec,process,123,"/sdk/dotnet",context,ENV),45)


if __name__ == "__main__": unittest.main()
