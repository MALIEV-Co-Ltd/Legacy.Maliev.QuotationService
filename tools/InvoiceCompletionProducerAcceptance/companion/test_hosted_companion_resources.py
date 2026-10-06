"""Pure observation/mutation/orchestration controls; zero actual Docker, TCP, SDK or database work."""
import copy
import base64
from dataclasses import replace
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest
from unittest.mock import MagicMock, patch

import hosted_companion_resources as h

NOW = datetime(2026,10,6,12,0,0,tzinfo=timezone.utc)
CTX = h.Context("37449256173",1,"a"*40,"c821-11111111-1111-4111-8111-111111111111",
                "2026-10-06T11:50:00+00:00","2026-10-06T12:20:00+00:00")
ENV = {"GITHUB_ACTIONS":"true","GITHUB_RUN_ID":CTX.run_id,"GITHUB_RUN_ATTEMPT":"1",
       "C821_COMPANION_PROFILE":h.ENVIRONMENT}
LEASE = h.ContainerLease("scanner","b"*64,"reviewed/clamd@sha256:"+"c"*64,
                         "2026-10-06T11:51:00.123456789Z","2026-10-06T11:51:01.123456789Z",
                         "127.0.0.1",33101,3310,"d"*64)


def observations():
    published = {"3310/tcp":[{"HostIp":"127.0.0.1","HostPort":"33101"}]}
    container = {"Id":LEASE.container_id,"Created":LEASE.created_utc,"Image":"sha256:"+"e"*64,
                 "State":{"StartedAt":LEASE.started_utc,"Restarting":False,"Paused":False,"Running":True,"Pid":234},
                 "Config":{"Image":LEASE.image_reference,"Labels":CTX.labels("scanner")},
                 "HostConfig":{"Privileged":False,"ReadonlyRootfs":True,"CapDrop":["ALL"],"CapAdd":[],
                               "SecurityOpt":["no-new-privileges:true"],"Memory":1024**3,"NanoCpus":1_000_000_000,
                               "RestartPolicy":{"Name":"no"},"AutoRemove":False,"Tmpfs":{"/tmp":"size=67108864"},
                               "NetworkMode":LEASE.network_id,"PidMode":"","IpcMode":"private","CgroupnsMode":"private",
                               "PortBindings":copy.deepcopy(published)},
                 "Mounts":[],"NetworkSettings":{"Ports":published,"Networks":{"owned":{"NetworkID":LEASE.network_id}}}}
    image = {"Id":container["Image"],"RepoDigests":[LEASE.image_reference]}
    network = {"Id":LEASE.network_id,"Internal":True,"Driver":"bridge","Created":"2026-10-06T11:50:01Z",
               "Labels":{k:v for k,v in CTX.labels("scanner").items() if k != h.LABEL_PREFIX+"role"}}
    return container,image,network


class ContextControls(unittest.TestCase):
    def test_current_exact_hosted_lease(self):
        CTX.validate(ENV,NOW)

    def test_expired_admission_is_denied_but_cleanup_remains_allowed(self):
        later = NOW+timedelta(minutes=30)
        with self.assertRaises(h.AdmissionError): CTX.validate(ENV,later)
        CTX.validate(ENV,later,cleanup=True)

    def test_overlong_or_future_issued_admission(self):
        for context in [replace(CTX,expires_utc="2026-10-06T12:20:01Z"),replace(CTX,issued_utc="2026-10-06T12:01:00Z")]:
            with self.subTest(context=context):
                with self.assertRaises(h.AdmissionError): context.validate(ENV,NOW)

    def test_missing_or_mismatched_run_attempt_profile(self):
        for key,value in [("GITHUB_ACTIONS","false"),("GITHUB_RUN_ID","1"),("GITHUB_RUN_ATTEMPT","2"),
                          ("C821_COMPANION_PROFILE","Production"),("C821_COMPANION_PROFILE",None)]:
            env = ENV.copy()
            if value is None: del env[key]
            else: env[key] = value
            with self.subTest(key=key,value=value):
                with self.assertRaises(h.AdmissionError): CTX.validate(env,NOW)

    def test_noncanonical_lease_and_boolean_attempt(self):
        for context in [replace(CTX,lease_id="c821-"+"0"*32),replace(CTX,lease_id=CTX.lease_id.upper()),replace(CTX,attempt=True)]:
            with self.subTest(context=context):
                with self.assertRaises(h.AdmissionError): context.validate(ENV,NOW)


class OriginJsonControls(unittest.TestCase):
    def test_explicit_ipv4_ipv6_origins(self):
        self.assertEqual(h.origin("http://127.0.0.1:80"),("127.0.0.1",80))
        self.assertEqual(h.origin("http://[::1]:33101"),("::1",33101))

    def test_unsafe_origin_forms(self):
        for value in ["http://localhost:80","http://127.0.0.1","http://127.0.0.1:80/","http://127.0.0.1:80?q=x",
                      "http://user@127.0.0.1:80","http://127.0.0.1:80#x","https://127.0.0.1:80",
                      "http://127.0.0.2:80","http://127.0.0.1:65536","http://127.0.0.1:080"]:
            with self.subTest(value=value):
                with self.assertRaises(h.AdmissionError): h.origin(value)

    def test_duplicate_nested_json_and_nonfinite_rejected(self):
        for value in [b'{"a":{"runId":1,"runId":2}}',b'{"a":NaN}',b'\xff',b'{} trailing']:
            with self.subTest(value=value):
                with self.assertRaises(h.AdmissionError): h.exact_json(value)
        self.assertEqual(h.exact_json(b'{"schemaVersion":1}'),{"schemaVersion":1})

    def test_hierarchical_config_keeps_nested_identity(self):
        config = {h.SECTION:{"Enabled":True,"Admission":{"storageEndpointIdentity":{"containerId":"b"*64},
                                                         "scannerDatabaseIdentity":{"readinessReceiptSha256":"c"*64}}}}
        values = h.configuration_environment(config)
        self.assertEqual(values[h.SECTION+"__Enabled"],"true")
        self.assertEqual(values[h.SECTION+"__Admission__storageEndpointIdentity__containerId"],"b"*64)
        self.assertEqual(values[h.SECTION+"__Admission__scannerDatabaseIdentity__readinessReceiptSha256"],"c"*64)

    def test_configuration_alias_or_wrong_section_rejected(self):
        for value in [{"Production":{}},{h.SECTION:{"Enabled":True,"Admission":{"a__b":1}}}]:
            with self.subTest(value=value):
                with self.assertRaises(h.AdmissionError): h.configuration_environment(value)


class ResourceObservationControls(unittest.TestCase):
    def test_exact_finite_disposable_engine_observations(self):
        result = h.validate_observations(CTX,LEASE,*observations(),NOW)
        self.assertEqual(result["containerId"],LEASE.container_id)
        self.assertEqual(result["imageDigest"],"sha256:"+"c"*64)

    def test_identity_resource_isolation_mutations(self):
        mutations = [
            ((0,"Id"),"f"*64),((0,"Created"),"2026-10-06T11:51:00.123456780Z"),
            ((0,"State","StartedAt"),"2026-10-06T11:51:01.123456780Z"),((0,"State","Running"),False),
            ((0,"State","Pid"),0),((0,"State","Restarting"),True),((0,"State","Paused"),True),
            ((0,"Config","Labels",h.LABEL_PREFIX+"lease"),"foreign"),
            ((0,"Config","Labels",h.LABEL_PREFIX+"file-source"),"f"*40),
            ((0,"Config","Image"),"reviewed/clamd:latest"),((1,"Id"),"sha256:"+"f"*64),((1,"RepoDigests"),[]),
            ((0,"HostConfig","Privileged"),True),((0,"HostConfig","ReadonlyRootfs"),False),
            ((0,"HostConfig","CapDrop"),[]),((0,"HostConfig","CapAdd"),["SYS_ADMIN"]),
            ((0,"HostConfig","SecurityOpt"),[]),((0,"HostConfig","Memory"),0),((0,"HostConfig","NanoCpus"),0),
            ((0,"HostConfig","RestartPolicy","Name"),"always"),((0,"HostConfig","AutoRemove"),True),
            ((0,"HostConfig","NetworkMode"),"host"),((0,"HostConfig","PidMode"),"host"),
            ((0,"HostConfig","IpcMode"),"host"),((0,"HostConfig","CgroupnsMode"),"host"),
            ((0,"Mounts"),[{"Type":"volume","Destination":"/data","Source":"persistent"}]),
            ((0,"HostConfig","Tmpfs"),{"/var/lib/clamav":"size=67108864"}),
            ((0,"HostConfig","PortBindings"),{"3310/tcp":[{"HostIp":"0.0.0.0","HostPort":"33101"}]}),
            ((0,"NetworkSettings","Ports"),{"3310/tcp":[{"HostIp":"127.0.0.1","HostPort":"33102"}]}),
            ((0,"NetworkSettings","Networks"),{"first":{"NetworkID":LEASE.network_id},"foreign":{"NetworkID":"f"*64}}),
            ((2,"Internal"),False),((2,"Created"),"2026-10-05T00:00:00Z"),
            ((2,"Labels",h.LABEL_PREFIX+"persistent"),"true"),
        ]
        for keys,value in mutations:
            rows = observations(); cursor = rows[keys[0]]
            for key in keys[1:-1]: cursor = cursor[key]
            cursor[keys[-1]] = value
            with self.subTest(path=keys):
                with self.assertRaises(h.AdmissionError): h.validate_observations(CTX,LEASE,*rows,NOW)

    def test_bounded_tmpfs_is_disposable_not_volume(self):
        rows = observations(); rows[0]["Mounts"] = [{"Type":"tmpfs","Destination":"/tmp","Source":""}]
        h.validate_observations(CTX,LEASE,*rows,NOW)

    def test_real_observer_queries_exact_ids_only(self):
        rows = observations()
        with patch.object(h,"docker_one",side_effect=rows) as observer:
            h.observe(CTX,LEASE,ENV,NOW)
            self.assertEqual(observer.call_args_list[0].args,("container",LEASE.container_id))
            self.assertEqual(observer.call_args_list[1].args,("image",rows[0]["Image"]))
            self.assertEqual(observer.call_args_list[2].args,("network",LEASE.network_id))


class CleanupControls(unittest.TestCase):
    def test_exact_owned_container_stop_remove_absence(self):
        stopped = observations()[0]; stopped["State"].update(Running=False,Pid=0)
        with patch.object(h,"observe") as observe,patch.object(h,"require_unused") as unused,\
             patch.object(h,"docker_one",return_value=stopped),patch.object(h,"command",side_effect=[b"",b"",b""]) as action:
            h.cleanup_owned(CTX,LEASE,ENV,NOW)
            self.assertEqual(observe.call_count,2); self.assertEqual(unused.call_count,2)
            self.assertEqual(action.call_args_list[0].args[0],["docker","container","stop","--time","10",LEASE.container_id])
            self.assertEqual(action.call_args_list[1].args[0],["docker","container","rm",LEASE.container_id])
            self.assertNotIn("--force",str(action.call_args_list))

    def test_active_client_prevents_any_stop(self):
        with patch.object(h,"observe"),patch.object(h,"require_unused",side_effect=h.AdmissionError("active")),patch.object(h,"command") as action:
            with self.assertRaises(h.AdmissionError): h.cleanup_owned(CTX,LEASE,ENV,NOW)
            action.assert_not_called()

    def test_changed_identity_prevents_any_stop(self):
        with patch.object(h,"observe",side_effect=h.AdmissionError("ownership")),patch.object(h,"command") as action:
            with self.assertRaises(h.AdmissionError): h.cleanup_owned(CTX,LEASE,ENV,NOW)
            action.assert_not_called()

    def test_not_exited_is_preserved_without_removal(self):
        with patch.object(h,"observe"),patch.object(h,"require_unused"),\
             patch.object(h,"docker_one",return_value=observations()[0]),patch.object(h,"command",return_value=b"") as action:
            with self.assertRaises(h.AdmissionError): h.cleanup_owned(CTX,LEASE,ENV,NOW)
            self.assertEqual(action.call_count,1)

    def test_remaining_or_unavailable_inventory_is_not_absence(self):
        stopped = observations()[0]; stopped["State"].update(Running=False,Pid=0)
        for inventory in [(LEASE.container_id+"\n").encode(),b"unexpected engine error"]:
            with self.subTest(inventory=inventory),patch.object(h,"observe"),patch.object(h,"require_unused"),\
                 patch.object(h,"docker_one",return_value=stopped),patch.object(h,"command",side_effect=[b"",b"",inventory]):
                with self.assertRaises(h.AdmissionError): h.cleanup_owned(CTX,LEASE,ENV,NOW)


class SocketProtocolControls(unittest.TestCase):
    def test_ipv4_clients_and_other_ports(self):
        header = " sl local_address rem_address st\n"
        busy = header+"0: 0100007F:814D 0100007F:C350 01 0\n"  # 33101
        self.assertTrue(h.connected_clients([busy],"127.0.0.1",33101))
        self.assertFalse(h.connected_clients([busy],"127.0.0.1",33102))
        self.assertFalse(h.connected_clients([busy.replace(" 01 "," 0A ")],"127.0.0.1",33101))

    def test_ipv6_remote_client_and_malformed_table(self):
        header = "sl local_address remote_address st\n"
        address = "00000000000000000000000001000000"
        self.assertTrue(h.connected_clients([header+f"0: {address}:C350 {address}:814D 01 0\n"],"::1",33101))
        with self.assertRaises(h.AdmissionError): h.connected_clients(["missing"],"::1",33101)

    def test_network_byte_order_instream_and_terminator(self):
        data = h.stream_request(b"benign")
        self.assertEqual(data,b"zINSTREAM\0"+struct.pack("!I",6)+b"benign"+b"\0"*4)
        with self.assertRaises(h.AdmissionError): h.stream_request(b"a"*4097)

    def test_actual_version_requires_loaded_database(self):
        self.assertEqual(h.parse_version("ClamAV 1.5.2/27780/Mon Oct  6 12:00:00 2026"),("1.5.2","27780"))
        for value in ["PONG","ClamAV 1.5.2/0/not loaded","ClamAV 1.5.2","ClamAV 1.5.2/27780/x\nspoof"]:
            with self.subTest(value=value):
                with self.assertRaises(h.AdmissionError): h.parse_version(value)

    def test_per_file_hash_map_matches_actual_database_set(self):
        paths = ["/var/lib/clamav/main.cvd","/var/lib/clamav/daily.cld"]
        text = "a"*64+"  "+paths[0]+"\n"+"b"*64+"  "+paths[1]+"\n"
        self.assertEqual(h.database_hash_map(paths,text),{"main.cvd":"a"*64,"daily.cld":"b"*64})
        for invalid in [text+text.splitlines()[0]+"\n",text.replace("daily.cld","bytecode.cld"),text.replace("a"*64,"A"*64),""]:
            with self.subTest(invalid=invalid):
                with self.assertRaises(h.AdmissionError): h.database_hash_map(paths,invalid)


class HelperSourceControls(unittest.TestCase):
    def test_composition_emits_nested_section_and_independent_receipt(self):
        storage = replace(LEASE,role="storage",container_id="f"*64,host_port=45001,container_port=4443,
                          image_reference="reviewed/storage@sha256:"+"1"*64)
        database = {"engineVersion":"1.5.2","loadedDatabaseVersion":"27780",
                    "databaseFilesSha256":{"main.cvd":"a"*64,"daily.cld":"b"*64},
                    "observedUtc":NOW.isoformat(),"readinessReceiptSha256":"c"*64}
        with patch.object(h,"verify_file_source",return_value={"fileSourceSha":CTX.file_sha}),\
             patch.object(h,"observe",side_effect=[{"kind":"container","containerId":storage.container_id},
                                                   {"kind":"container","containerId":LEASE.container_id},{},{}]) as observe,\
             patch.object(h,"scanner_readiness",return_value=(database,{"actualEngineClaim":False})),\
             patch.object(h,"datetime") as clock:
            clock.now.return_value=NOW; clock.fromisoformat.side_effect=datetime.fromisoformat
            profile,receipt = h.build_file_configuration(CTX,storage,LEASE,ENV,NOW,"/source","e"*40,"/source/file.dll","a"*64)
            admission = profile[h.SECTION]["Admission"]
            self.assertEqual(set(profile[h.SECTION]),{"Enabled","Admission"})
            self.assertEqual(admission["scannerDatabaseIdentity"]["databaseFilesSha256"],database["databaseFilesSha256"])
            self.assertEqual(admission["storageEndpointIdentity"]["containerId"],storage.container_id)
            self.assertEqual(admission["storageOrigin"],"http://127.0.0.1:45001")
            self.assertNotIn("ScannerReady",admission); self.assertNotIn("StorageResourceId",admission)
            self.assertEqual(observe.call_count,4)
            self.assertIn("resource cleanup",receipt["notProven"])

    def test_composition_rejects_shared_or_mismatched_endpoints_before_source_or_engine(self):
        for storage in [replace(LEASE,role="storage"),replace(LEASE,role="storage",container_id="f"*64,host_ip="::1")]:
            with self.subTest(storage=storage),patch.object(h,"verify_file_source") as source,patch.object(h,"observe") as engine:
                with self.assertRaises(h.AdmissionError):
                    h.build_file_configuration(CTX,storage,LEASE,ENV,NOW,"/source","e"*40,"/source/file.dll","a"*64)
                source.assert_not_called(); engine.assert_not_called()

    def test_helper_timeout_and_engine_failure_are_closed(self):
        for error in [OSError("engine unavailable"),subprocess.TimeoutExpired(["docker"],10)]:
            with self.subTest(error=error),patch.object(h.sys,"platform","linux"),patch.object(h.subprocess,"Popen",side_effect=error):
                with self.assertRaises(h.AdmissionError): h.command(["docker","container","inspect",LEASE.container_id])

    def test_actual_source_and_dll_readback(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve(); dll = root/"File.Api.dll"; dll.write_bytes(b"synthetic-hash-control-not-CLR-proof")
            digest = hashlib.sha256(dll.read_bytes()).hexdigest()
            with patch.object(h,"command",side_effect=[CTX.file_sha.encode(),b"e"*40,b""]):
                result = h.verify_file_source(CTX,root,"e"*40,dll,digest)
                self.assertEqual(result["fileExecutableSha256"],digest)
            with patch.object(h,"command",side_effect=[CTX.file_sha.encode(),b"e"*40,b" M unrelated\n"]):
                with self.assertRaises(h.AdmissionError): h.verify_file_source(CTX,root,"e"*40,dll,digest)
            with patch.object(h,"command",side_effect=[CTX.file_sha.encode(),b"e"*40,b""]):
                with self.assertRaises(h.AdmissionError): h.verify_file_source(CTX,root,"e"*40,dll,"f"*64)


class FileHandshakeControls(unittest.TestCase):
    # Opaque public-key carrier shape only. Not an RSA import/signature/native File assertion.
    KEY = base64.b64encode(b"\x30"+b"opaque-public-carrier"*15).decode("ascii")
    BODY = json.dumps({"Algorithm":"GOOG4-RSA-SHA256","PublicKey":KEY}).encode()
    PROCESS = h.FileProcessLease(234,12345,"/source/File.Api.dll","a"*64,"/sdk/dotnet","b"*64,"127.0.0.1",45003)

    def setUp(self):
        class FixedUtc(datetime):
            @classmethod
            def now(cls,tz=None): return NOW
        clock=patch.object(h,"datetime",FixedUtc); clock.start(); self.addCleanup(clock.stop)

    def test_exact_pascal_handshake_and_public_fingerprint(self):
        value,digest = h.parse_signing_handshake(self.BODY)
        self.assertEqual(value["PublicKey"],self.KEY)
        self.assertEqual(digest,hashlib.sha256(base64.b64decode(self.KEY)).hexdigest())

    def test_casing_private_fields_algorithm_and_invalid_public_carriers_rejected(self):
        valid = {"Algorithm":"GOOG4-RSA-SHA256","PublicKey":self.KEY}
        cases = [{"algorithm":valid["Algorithm"],"publicKey":self.KEY},{**valid,"PrivateKey":"never-admitted"},
                 {**valid,"Algorithm":"GOOG4-HMAC-SHA256"},{**valid,"PublicKey":"not-base64"},
                 {**valid,"PublicKey":base64.b64encode(b"tiny").decode()},{**valid,"PublicKey":None}]
        for value in cases:
            with self.subTest(keys=list(value)):
                with self.assertRaises(h.AdmissionError): h.parse_signing_handshake(json.dumps(value).encode())

    def test_duplicate_handshake_field_rejected(self):
        with self.assertRaises(h.AdmissionError):
            h.parse_signing_handshake(b'{"Algorithm":"GOOG4-RSA-SHA256","Algorithm":"other","PublicKey":"x"}')

    def test_kernel_generation_handles_comm_spaces_and_parentheses(self):
        fields = [b"S"]+[b"0"]*18+[b"12345"]
        data = b"234 (dotnet (accepted)) "+b" ".join(fields)
        self.assertEqual(h.process_start_ticks(data,234),12345)
        with self.assertRaises(h.AdmissionError): h.process_start_ticks(data,235)
        with self.assertRaises(h.AdmissionError): h.process_start_ticks(data.replace(b") S ",b") Z "),234)

    def test_process_environment_aliases_duplicates_and_wrong_lease_rejected(self):
        self.assertEqual(h.process_environment(b"A=x\0B=y\0"),{"A":"x","B":"y"})
        with self.assertRaises(h.AdmissionError): h.process_environment(b"A=x\0A=y\0")
        for actual in [{"ASPNETCORE_ENVIRONMENT":"Production"},
                       {"ASPNETCORE_ENVIRONMENT":h.ENVIRONMENT,"aspnetcore_environment":h.ENVIRONMENT}]:
            with self.subTest(actual=actual):
                with self.assertRaises(h.AdmissionError): h.consumed(actual,"ASPNETCORE_ENVIRONMENT",h.ENVIRONMENT)

    def test_literal_listener_inode_rejects_wildcard_and_duplicates(self):
        header = "sl local_address rem_address st tx tr retr uid timeout inode\n"
        # Explicit45003 is AFCB, with ten standard kernel columns.
        line = "0: 0100007F:AFCB 00000000:0000 0A 0 0 0 0 0 999\n"
        self.assertEqual(h.listening_inode([header+line],"127.0.0.1",45003),"999")
        with self.assertRaises(h.AdmissionError): h.listening_inode([header+line+line],"127.0.0.1",45003)
        with self.assertRaises(h.AdmissionError): h.listening_inode([header+line.replace("0100007F","00000000")],"127.0.0.1",45003)

    def connection(self,status=200,body=None):
        response = MagicMock(); response.status=status
        response.getheader.side_effect=lambda name:{"Content-Type":"application/json; charset=utf-8","Content-Length":str(len(self.BODY))}.get(name)
        response.read.return_value=self.BODY if body is None else body
        connection=MagicMock(); connection.getresponse.return_value=response
        return connection

    def test_authenticated_fixed_route_observed_twice_and_connection_closed(self):
        connection=self.connection(); identity={"pid":234,"kernelStartTicks":12345}
        with patch.object(h,"observe_file_process",return_value=identity) as observe,\
             patch.object(h,"deadline_http_connection",return_value=connection):
            value,receipt=h.read_actual_signing_key(CTX,self.PROCESS,ENV,NOW,"synthetic.jwt.signature")
            self.assertEqual(observe.call_count,2)
            self.assertEqual(connection.request.call_args.args,("GET","/file/acceptance/signing-key"))
            self.assertEqual(connection.request.call_args.kwargs["headers"]["Authorization"],"Bearer synthetic.jwt.signature")
            self.assertNotIn("synthetic.jwt.signature",json.dumps(receipt)); connection.close.assert_called_once()

    def test_redirect_or_expired_503_fails_without_following_and_closes(self):
        for status in [301,302,307,308,401,503]:
            connection=self.connection(status)
            with self.subTest(status=status),patch.object(h,"observe_file_process",return_value={"pid":234}),\
                 patch.object(h,"deadline_http_connection",return_value=connection):
                with self.assertRaises(h.AdmissionError): h.read_actual_signing_key(CTX,self.PROCESS,ENV,NOW,"synthetic.jwt.signature")
                self.assertEqual(connection.request.call_count,1); connection.close.assert_called_once()

    def test_changed_file_generation_rejects_received_key(self):
        connection=self.connection()
        with patch.object(h,"observe_file_process",side_effect=[{"pid":234,"kernelStartTicks":1},{"pid":234,"kernelStartTicks":2}]),\
             patch.object(h,"deadline_http_connection",return_value=connection):
            with self.assertRaises(h.AdmissionError): h.read_actual_signing_key(CTX,self.PROCESS,ENV,NOW,"synthetic.jwt.signature")
            connection.close.assert_called_once()

    def test_bearer_prefix_and_header_injection_rejected_before_observation(self):
        for token in ["Bearer synthetic.jwt.signature","synthetic.jwt.signature\r\nInjected: value",""]:
            with self.subTest(tokenLength=len(token)),patch.object(h,"observe_file_process") as observe:
                with self.assertRaises(h.AdmissionError): h.read_actual_signing_key(CTX,self.PROCESS,ENV,NOW,token)
                observe.assert_not_called()


if __name__ == "__main__":
    unittest.main()
