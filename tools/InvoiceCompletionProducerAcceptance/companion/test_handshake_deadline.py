"""Controlled slow-drip byte transport through the real HTTP parser; no server, socket or child created."""
import io
from dataclasses import replace
from datetime import datetime, timedelta
import unittest
from unittest.mock import patch

import hosted_companion_resources as h
from test_hosted_companion_resources import CTX, ENV, NOW
import test_hosted_companion_resources as fixtures


class ControlledSocket:
    def __init__(self, wire, clock, slow_after, delay):
        self.wire=wire; self.clock=clock; self.slow_after=slow_after; self.delay=delay
        self.timeout=5; self.offset=0; self.closed=False; self.raw=None; self.timeouts=[]

    def settimeout(self,value): self.timeout=value; self.timeouts.append(value)
    def connect(self,address): self.address=address
    def sendall(self,data): self.sent=data
    def close(self): self.closed=True

    def makefile(self,mode,buffering=0):
        sock=self
        class Raw(io.RawIOBase):
            def readable(self): return True
            def readinto(self,buffer):
                if sock.offset == len(sock.wire): return 0
                delay=sock.delay if sock.offset >= sock.slow_after else 0
                if delay > sock.timeout:
                    sock.clock[0]+=sock.timeout
                    raise TimeoutError("controlled receive consumed its remaining total budget")
                sock.clock[0]+=delay
                buffer[0]=sock.wire[sock.offset]; sock.offset+=1
                return 1
        self.raw=Raw(); return self.raw


class HandshakeDeadlineControls(unittest.TestCase):
    def exercise(self,slow_headers=False,delay=1,body=None,context=CTX):
        payload=fixtures.FileHandshakeControls.BODY if body is None else body
        header=("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+str(len(payload))+"\r\nConnection: close\r\n\r\n").encode()
        clock=[0.0]; sock=ControlledSocket(header+payload,clock,0 if slow_headers else len(header),delay)
        class ControlledUtc(datetime):
            @classmethod
            def now(cls,tz=None): return NOW+timedelta(seconds=clock[0])
        identity={"pid":234,"kernelStartTicks":12345}
        with patch.object(h.time,"monotonic",side_effect=lambda:clock[0]),\
             patch.object(h,"datetime",ControlledUtc),\
             patch.object(h.socket,"socket",return_value=sock),\
             patch.object(h,"observe_file_process",return_value=identity) as observe:
            try:
                value=h.read_actual_signing_key(context,fixtures.FileHandshakeControls.PROCESS,ENV,NOW,"synthetic.jwt.signature")
                failed=False
            except h.AdmissionError: value=None; failed=True
        if sock.raw is not None: self.assertTrue(sock.closed)
        if sock.raw is not None: self.assertTrue(sock.raw.closed)
        return failed,value,sock,clock[0],observe.call_count

    def test_slow_drip_body_cannot_reset_total_five_second_deadline(self):
        failed,_,sock,elapsed,observations=self.exercise()
        self.assertTrue(failed); self.assertEqual(elapsed,5); self.assertEqual(observations,1)
        self.assertGreater(len(sock.timeouts),2); self.assertLess(sock.timeouts[-1],5)

    def test_slow_drip_headers_use_the_same_total_deadline_and_close(self):
        failed,_,_,elapsed,observations=self.exercise(slow_headers=True)
        self.assertTrue(failed); self.assertEqual(elapsed,5); self.assertEqual(observations,1)

    def test_prompt_real_http_parser_reply_observes_identity_twice_and_closes(self):
        failed,value,_,elapsed,observations=self.exercise(delay=0)
        self.assertFalse(failed); self.assertEqual(elapsed,0); self.assertEqual(observations,2)
        self.assertEqual(value[0]["Algorithm"],"GOOG4-RSA-SHA256")

    def test_invalid_json_body_closes_parser_pipe_and_connection(self):
        failed,_,_,_,observations=self.exercise(delay=0,body=b"not json")
        self.assertTrue(failed); self.assertEqual(observations,1)

    def test_expired_budget_rejects_before_socket_creation(self):
        with patch.object(h.time,"monotonic",return_value=5),patch.object(h.socket,"socket") as sock:
            with self.assertRaises(h.AdmissionError): h.deadline_http_connection("127.0.0.1",45003,5)
        sock.assert_not_called()

    def test_near_expiry_slow_drip_ends_at_lease_and_closes_all_handles(self):
        context=replace(CTX,expires_utc=(NOW+timedelta(seconds=2)).isoformat())
        failed,_,sock,elapsed,observations=self.exercise(context=context)
        self.assertTrue(failed); self.assertEqual(elapsed,2); self.assertEqual(observations,1)
        self.assertTrue(sock.closed and sock.raw.closed); self.assertLessEqual(max(sock.timeouts),2)

    def test_expired_lease_after_before_observation_rejects_before_socket(self):
        class ExpiredUtc(datetime):
            @classmethod
            def now(cls,tz=None): return NOW+timedelta(minutes=30)
        with patch.object(h,"datetime",ExpiredUtc),patch.object(h,"observe_file_process",return_value={"pid":234}),\
             patch.object(h.socket,"socket") as sock,patch.object(h,"deadline_http_connection") as connection:
            with self.assertRaisesRegex(h.AdmissionError,"lease expired before transport"):
                h.read_actual_signing_key(CTX,fixtures.FileHandshakeControls.PROCESS,ENV,NOW,"synthetic.jwt.signature")
        sock.assert_not_called(); connection.assert_not_called()


if __name__ == "__main__": unittest.main()
