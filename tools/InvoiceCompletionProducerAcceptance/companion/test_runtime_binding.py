"""Mocked controls only: no child, Docker endpoint, scanner or SDK is started."""
from dataclasses import replace
import hashlib
import subprocess
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

import hosted_companion_resources as h
from test_hosted_companion_resources import CTX, ENV, LEASE, NOW, observations


class BoundedCommandControls(unittest.TestCase):
    def exercise(self, chunks, maximum=4, exit_code=0, timeout=False, forced=False):
        process = MagicMock()
        process.stdout.fileno.return_value=11; process.stderr.fileno.return_value=12
        process.returncode=exit_code
        process.poll.return_value=0
        selector=MagicMock()
        selector.get_map.side_effect=[True]*len(chunks)+[False]
        selector.select.side_effect=[[(SimpleNamespace(fileobj=process.stdout),1)]]*len(chunks)
        if timeout: selector.select.side_effect=h.AdmissionError("deadline")
        if forced:
            process.poll.side_effect=[None,0]
            process.wait.side_effect=[subprocess.TimeoutExpired("owned",2),0]
        with patch.object(h.sys,"platform","linux"),patch.object(h.subprocess,"Popen",return_value=process),\
             patch.object(h.selectors,"DefaultSelector",return_value=selector),patch.object(h.os,"set_blocking"),\
             patch.object(h.os,"read",side_effect=chunks) as read:
            try: result=h.command(["synthetic-owned-helper"],maximum=maximum)
            except h.AdmissionError: result=None
        self.assertTrue(process.stdout.close.called and process.stderr.close.called and selector.close.called)
        return result,process,read

    def test_bounded_stream_read_and_reap(self):
        result,process,read=self.exercise([b"abc",b""])
        self.assertEqual(result,b"abc"); process.wait.assert_called_once()
        self.assertLessEqual(max(call.args[1] for call in read.call_args_list),5)

    def test_output_overflow_aborts_before_buffering_and_closes(self):
        result,_,read=self.exercise([b"12345"])
        self.assertIsNone(result); self.assertEqual(read.call_count,1)

    def test_deadline_cleanup_forces_only_held_child_and_reaps(self):
        result,process,_=self.exercise([b""],timeout=True,forced=True)
        self.assertIsNone(result); process.terminate.assert_called_once(); process.kill.assert_called_once()
        self.assertEqual(process.wait.call_count,2)

    def test_nonzero_exit_is_closed(self):
        result,_,_=self.exercise([b""],exit_code=1)
        self.assertIsNone(result)

    def test_invalid_limits_cannot_start_child(self):
        with patch.object(h.sys,"platform","linux"),patch.object(h.subprocess,"Popen") as child:
            for timeout,maximum in [(0,4),(61,4),(1,0),(1,1048577)]:
                with self.assertRaises(h.AdmissionError): h.command(["ignored"],timeout,maximum)
            child.assert_not_called()


CONFIG=b"DatabaseDirectory /var/lib/clamav\nSelfCheck 0\nLogFile /tmp/clamd.log\n"
PLAN=h.ScannerPlan("/usr/sbin/clamd","a"*64,
                   ("/usr/sbin/clamd","--foreground","--config-file=/etc/clamd.conf"),
                   "/etc/clamd.conf",hashlib.sha256(CONFIG).hexdigest(),
                   {"main.cvd":"b"*64,"daily.cld":"c"*64},"/tmp/clamd.log")


class ScannerBindingControls(unittest.TestCase):
    def observed(self, overrides=None, plan=PLAN, generation=None):
        table=b" sl local_address rem_address st tx_queue rx_queue tr tm retr uid inode\n0: 00000000:0CEE 00000000:0000 0A 0 0 0 0 0 876\n"
        values={ ("cat","/proc/net/tcp"):table,
                 ("cat","/proc/net/tcp6"):table.splitlines()[0]+b"\n",
                 ("ls","-1","/proc"):b"42\nnet\n",
                 ("ls","-1","/proc/42/fd"):b"3\n",
                 ("readlink","/proc/42/fd/3"):b"socket:[876]\n",
                 ("cat","/proc/42/stat"):b"synthetic stat readback",
                 ("readlink","/proc/42/exe"):b"/usr/sbin/clamd\n",
                 ("cat","/proc/42/cmdline"):b"/usr/sbin/clamd\0--foreground\0--config-file=/etc/clamd.conf\0",
                 ("sha256sum","/proc/42/exe"):("a"*64+"  /proc/42/exe\n").encode(),
                 ("readlink","/proc/42/root"):b"/\n",
                 ("readlink","-f","/etc/clamd.conf"):b"/etc/clamd.conf\n",
                 ("cat","/proc/42/root/etc/clamd.conf"):CONFIG,
                 ("cat","/proc/42/root/tmp/clamd.log"):b"Reading databases from /var/lib/clamav\n"}
        values.update(overrides or {})
        def execute(args,**kwargs):
            value=values[tuple(args[4:])]
            if isinstance(value,Exception): raise value
            if callable(value): return value()
            return value
        with patch.object(h,"command",side_effect=execute),\
             patch.object(h,"process_start_ticks",return_value=123,side_effect=generation):
            return h.scanner_process(LEASE,plan)

    def test_disappeared_enumeration_helper_is_skipped_only_after_positive_inventory(self):
        calls=iter([b"42\n99\n",b"42\n100\n"])
        result=self.observed({("ls","-1","/proc"):lambda:next(calls),
                              ("cat","/proc/99/stat"):b"nonowner initial stat",
                              ("ls","-1","/proc/99/fd"):h.AdmissionError("helper exited")})
        self.assertEqual(result["pid"],42)

    def test_unreadable_live_nonowner_cannot_be_skipped(self):
        with self.assertRaises(h.AdmissionError):
            self.observed({("ls","-1","/proc"):b"42\n99\n",
                           ("cat","/proc/99/stat"):h.AdmissionError("permission denied")})

    def test_disappeared_listener_is_not_a_successful_admission(self):
        calls=iter([b"42\n",b"100\n"])
        with self.assertRaises(h.AdmissionError):
            self.observed({("ls","-1","/proc"):lambda:next(calls),
                           ("cat","/proc/42/stat"):h.AdmissionError("listener exited")})

    def test_listener_pid_reuse_during_discovery_is_rejected(self):
        with self.assertRaisesRegex(h.AdmissionError,"generation changed"):
            self.observed(generation=[123,999])

    def test_listener_generation_change_during_final_readback_is_rejected(self):
        with self.assertRaisesRegex(h.AdmissionError,"generation changed"):
            self.observed(generation=[123,123,999])

    def test_listener_descriptor_disappearance_during_final_readback_is_rejected(self):
        targets=iter([b"socket:[876]\n",b"socket:[999]\n"])
        with self.assertRaisesRegex(h.AdmissionError,"descriptor changed"):
            self.observed({("readlink","/proc/42/fd/3"):lambda:next(targets)})

    def test_actual_listener_executable_argv_config_and_generation_are_bound(self):
        result=self.observed()
        self.assertEqual(result["pid"],42); self.assertEqual(result["listenerInode"],"876")
        self.assertEqual(result["configurationSha256"],PLAN.configuration_sha256)

    def test_pinned_image_command_override_is_rejected(self):
        container,image,network=observations()
        container["Config"]["Cmd"]=["counterfeit-protocol-responder"]
        with self.assertRaises(h.AdmissionError): h.validate_observations(CTX,LEASE,container,image,network,NOW)

    def test_counterfeit_listener_binary_and_alternate_configuration_rejected(self):
        for changes in [{("readlink","/proc/42/exe"):b"/usr/bin/responder\n"},
                        {("cat","/proc/42/cmdline"):b"/usr/sbin/clamd\0--config-file=/etc/alternate.conf\0"},
                        {("cat","/proc/42/root/etc/clamd.conf"):CONFIG.replace(b"/var/lib/clamav",b"/other/database")}]:
            with self.subTest(changes=changes),self.assertRaises(h.AdmissionError): self.observed(changes)

    def test_same_version_alternate_database_bytes_rejected(self):
        commands=[b"/var/lib/clamav\n",b"/var/lib/clamav/main.cvd\n/var/lib/clamav/daily.cld\n",
                  ("b"*64+"  /var/lib/clamav/main.cvd\n"+"d"*64+"  /var/lib/clamav/daily.cld\n").encode()]
        with patch.object(h,"observe"),patch.object(h,"scanner_process",return_value={"pid":42}),\
             patch.object(h,"clamav_record",side_effect=["ClamAV 1.5.2/27780/date","stream: OK","stream: Eicar FOUND"]),\
             patch.object(h,"command",side_effect=commands):
            with self.assertRaisesRegex(h.AdmissionError,"database bytes"):
                h.scanner_readiness(CTX,LEASE,ENV,NOW,PLAN)

    def test_missing_runtime_plan_cannot_be_admitted(self):
        with self.assertRaises(h.AdmissionError): self.observed(plan=None)

    def test_missing_load_path_log_is_rejected(self):
        with self.assertRaises(h.AdmissionError):
            self.observed({("cat","/proc/42/root/tmp/clamd.log"):b"Reading databases from /other/database\n"})

    def test_mutable_configuration_location_is_rejected(self):
        plan=replace(PLAN,configuration="/tmp/clamd.conf",argv=(PLAN.executable,"--foreground","--config-file=/tmp/clamd.conf"))
        with self.assertRaises(h.AdmissionError): self.observed(plan=plan)


if __name__ == "__main__": unittest.main()
