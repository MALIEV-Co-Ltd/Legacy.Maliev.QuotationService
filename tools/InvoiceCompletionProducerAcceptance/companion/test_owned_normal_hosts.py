"""Source-only normal-host graph/memory/owned-stop controls; no actual child or service startup."""
from dataclasses import replace
import subprocess
import unittest
from unittest.mock import MagicMock,patch
import hosted_companion_resources as h
import owned_normal_hosts as n
from test_hosted_companion_resources import CTX,ENV


def specs():
    values=[]
    for index,owner in enumerate(sorted(n.OWNERS)):
        env={"ASPNETCORE_ENVIRONMENT":h.ENVIRONMENT if owner == "File" else "Production"}
        if owner == "File": env.update({h.SECTION+"__Enabled":"true",h.SECTION+"__Admission__fileSourceSha":CTX.file_sha})
        if owner == "Notification": env["Notifications__DeliveryIntentsEnabled"]="false"
        env["ASPNETCORE_URLS"]=f"{'http' if owner == 'File' else 'https'}://127.0.0.1:{45000+index}"
        identity=None
        if owner != "File":
            identity=n.tls.TlsIdentity("/task/tls","/task/tls/ca.pem","a"*64,
                                       "/task/tls/leaf.pem","b"*64,"/task/tls/key.pem","c"*64,"/task/tls/empty")
            env.update({"Kestrel__Certificates__Default__Path":identity.certificate_path,
                        "Kestrel__Certificates__Default__KeyPath":identity.private_key_path,
                        "SSL_CERT_FILE":identity.ca_path,"SSL_CERT_DIR":identity.trust_directory})
        values.append(n.HostSpec(owner,"/source/"+owner,CTX.file_sha if owner == "File" else "b"*40,"c"*40,
                                 "/source/"+owner+"/Api.dll","d"*64,"127.0.0.1",45000+index,env,256*1024**2,identity))
    return values


class NormalHostSourceControls(unittest.TestCase):
    def test_exact_graph_and_memory_threshold(self):
        self.assertEqual(n.validate_specs(specs(),CTX,2*1024**3,4*1024**3),3*1024**3)

    def test_existing_failed_memory_guard_is_never_lowered(self):
        with self.assertRaises(h.AdmissionError): n.validate_specs(specs(),CTX,5*1024**3,4*1024**3)

    def test_missing_owner_and_endpoint_overlap_rejected(self):
        for values in [specs()[:-1],[*specs()[:-1],replace(specs()[-1],host_port=specs()[0].host_port)]]:
            with self.subTest(count=len(values)):
                with self.assertRaises(h.AdmissionError): n.validate_specs(values,CTX,2*1024**3,4*1024**3)

    def test_file_profile_source_and_other_host_environment_mismatch_rejected(self):
        for role in ["File","Document"]:
            values=specs(); index=next(i for i,spec in enumerate(values) if spec.owner == role)
            env=values[index].environment.copy(); env["ASPNETCORE_ENVIRONMENT"]="Production" if role == "File" else h.ENVIRONMENT
            values[index]=replace(values[index],environment=env)
            with self.subTest(role=role):
                with self.assertRaises(h.AdmissionError): n.validate_specs(values,CTX,2*1024**3,4*1024**3)

    def scope(self,processes):
        scope=n.OwnedNormalHosts(CTX,ENV,specs(),"/sdk/dotnet","a"*64,2*1024**3)
        scope.owned=[[spec,process,12345,"observed"] for spec,process in zip(specs(),processes)]
        return scope

    def test_reverse_order_exact_owned_handles_gracefully_exit(self):
        events=[]; processes=[]
        for index in range(2):
            process=MagicMock(); process.pid=100+index; process.poll.side_effect=[None,0]
            process.terminate.side_effect=lambda i=index:events.append(i); processes.append(process)
        scope=self.scope(processes)
        with patch.object(h,"bounded_file",return_value=b"kernel"),patch.object(h,"process_start_ticks",return_value=12345): scope.close()
        self.assertEqual(events,[1,0]); self.assertTrue(scope.closed)
        for process in processes: process.kill.assert_not_called(); process.wait.assert_called_once_with(timeout=10)

    def test_changed_kernel_identity_is_preserved_without_signal(self):
        process=MagicMock(); process.pid=100; process.poll.return_value=None; scope=self.scope([process])
        with patch.object(h,"bounded_file",return_value=b"kernel"),patch.object(h,"process_start_ticks",return_value=999):
            with self.assertRaises(h.AdmissionError): scope.close()
        process.terminate.assert_not_called(); process.kill.assert_not_called(); self.assertFalse(scope.closed)

    def test_forced_exact_child_requires_second_generation_check(self):
        process=MagicMock(); process.pid=100; process.poll.side_effect=[None,0]
        process.wait.side_effect=[subprocess.TimeoutExpired(["exact-owned-child"],10),0]; scope=self.scope([process])
        with patch.object(h,"bounded_file",return_value=b"kernel"),patch.object(h,"process_start_ticks",return_value=12345) as generation: scope.close()
        self.assertEqual(generation.call_count,2); process.terminate.assert_called_once(); process.kill.assert_called_once()

    def test_failed_cleanup_still_attempts_other_exact_owned_children(self):
        one=MagicMock(); one.pid=100; one.poll.side_effect=[None,0]
        two=MagicMock(); two.pid=101; two.poll.return_value=None; scope=self.scope([one,two])
        with patch.object(h,"bounded_file",return_value=b"kernel"),patch.object(h,"process_start_ticks",side_effect=[999,12345]):
            with self.assertRaises(h.AdmissionError): scope.close()
        one.terminate.assert_called_once(); two.terminate.assert_not_called()

    def test_start_failure_before_kernel_capture_still_reaps_held_child(self):
        process=MagicMock(); process.pid=100; process.poll.side_effect=[None,0]
        scope=self.scope([process]); scope.owned[0][2]=None
        with patch.object(h,"bounded_file",side_effect=FileNotFoundError()): scope.close()
        process.terminate.assert_called_once(); process.wait.assert_called_once_with(timeout=10)
        self.assertTrue(scope.closed)

    def test_expiry_closes_children_and_raises_even_if_caller_catches(self):
        scope=self.scope([])
        with self.assertRaisesRegex(h.AdmissionError,"lease expired"): scope.expire(None,None)
        self.assertTrue(scope.closed)

    def test_owned_timer_is_cancelled_and_restored_at_exit(self):
        scope=self.scope([]); scope.timer_owned=True
        signals=MagicMock(); signals.ITIMER_REAL=0; signals.SIGALRM=14; signals.SIG_DFL=0
        with patch.object(n,"signal",signals):
            scope.__exit__(None,None,None)
        signals.setitimer.assert_called_once_with(0,0)
        signals.signal.assert_called_once_with(14,0)
        self.assertFalse(scope.timer_owned)

    def test_failed_exit_retains_expiry_owner_and_exact_failure_until_retry_exits(self):
        process=MagicMock(); process.pid=100; process.poll.return_value=None
        scope=self.scope([process]); scope.timer_owned=True
        signals=MagicMock(); signals.ITIMER_REAL=0; signals.SIGALRM=14; signals.SIG_DFL=0
        with patch.object(n,"signal",signals),patch.object(h,"bounded_file",return_value=b"kernel"), \
             patch.object(h,"process_start_ticks",return_value=999):
            with self.assertRaises(h.AdmissionError): scope.__exit__(None,None,None)
            self.assertEqual(signals.setitimer.call_args_list, [unittest.mock.call(0,0),unittest.mock.call(0,5)])
            signals.signal.assert_not_called()
            self.assertTrue(scope.timer_owned); self.assertFalse(scope.closed)
            self.assertEqual(scope.cleanup_failures,[(specs()[0].owner,100,"AdmissionError")])
            process.terminate.assert_not_called(); process.kill.assert_not_called()
            process.poll.return_value=0
            scope.close()
            signals.signal.assert_called_once_with(14,0)
            self.assertFalse(scope.timer_owned); self.assertTrue(scope.closed)
            self.assertEqual(scope.cleanup_failures,[])

    def test_pending_expiry_at_spawn_handoff_sees_and_reaps_exact_child(self):
        process=MagicMock(); process.pid=100; process.poll.side_effect=[None,0]
        scope=self.scope([])
        signals=MagicMock(); signals.SIG_BLOCK=0; signals.SIG_SETMASK=2; signals.SIGALRM=14
        def mask(mode,values):
            if mode == 0:
                self.assertEqual(values,{14})
                return {9}
            self.assertEqual(values,{9})
            self.assertIs(scope.owned[0][1],process)
            scope.expire(None,None)
        signals.pthread_sigmask.side_effect=mask
        with patch.object(n,"signal",signals),patch.object(n.subprocess,"Popen",return_value=process), \
             patch.object(h,"bounded_file",return_value=b"kernel"),patch.object(h,"process_start_ticks",return_value=12345):
            with self.assertRaisesRegex(h.AdmissionError,"lease expired"):
                scope.spawn_owned(specs()[0],{})
        process.terminate.assert_called_once(); process.wait.assert_called_once_with(timeout=10)
        process.kill.assert_not_called(); self.assertTrue(scope.closed)

    def test_preblocked_expiry_signal_rejected_without_changing_owner_mask_or_timer(self):
        signals=MagicMock(); signals.SIGALRM=14; signals.SIG_DFL=0; signals.SIG_BLOCK=0
        signals.getsignal.return_value=0; signals.getitimer.return_value=(0.0,0.0)
        signals.pthread_sigmask.return_value={14}
        with patch.object(n,"signal",signals):
            with self.assertRaisesRegex(h.AdmissionError,"unblocked"):
                n.require_unused_expiry_signal()
        signals.pthread_sigmask.assert_called_once_with(0,set())
        signals.setitimer.assert_not_called(); signals.signal.assert_not_called()

    def test_failed_cleanup_scope_cannot_launch_another_owner(self):
        scope=self.scope([]); scope.cleanup_failures=[("File",100,"AdmissionError")]
        with patch.object(n.subprocess,"Popen") as spawn:
            with self.assertRaisesRegex(h.AdmissionError,"failed/closed"):
                scope.start("Document")
        spawn.assert_not_called()


if __name__ == "__main__": unittest.main()
