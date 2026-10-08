import json
import socket
import struct
import threading
import time
import unittest
from unittest.mock import patch

from hosted_scanner_readiness import Scanner
from scanner_loopback_relay import LoopbackRelay


class RelayTests(unittest.TestCase):
    def test_complete_framed_bytes_reach_only_the_validated_backend(self):
        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        endpoint = listener.getsockname()
        observed = []
        def server():
            with listener, listener.accept()[0] as connection:
                def read(length):
                    result = b""
                    while len(result) < length:
                        result += connection.recv(length-len(result))
                    return result
                observed.append(read(10))
                payload = b""
                while True:
                    length = struct.unpack("!I", read(4))[0]
                    if not length:
                        break
                    payload += read(length)
                observed.append(payload)
                connection.sendall(b"stream: OK\0")
        thread = threading.Thread(target=server)
        thread.start()
        validations = []
        def validate():
            validations.append(True)
            return endpoint
        relay = LoopbackRelay(validate)
        scanner = Scanner()
        scanner.port = relay.endpoint[1]
        payload = b"disposable" * 15000
        try:
            self.assertEqual("clean", scanner.scan(payload))
        finally:
            receipt = relay.close()
            thread.join(timeout=2)
        self.assertEqual([b"zINSTREAM\0", payload], observed)
        self.assertEqual([True], validations)
        self.assertTrue(receipt["cleanupVerified"])
        self.assertFalse(thread.is_alive())

    def test_changed_backend_identity_fails_closed(self):
        relay = LoopbackRelay(lambda: (_ for _ in ()).throw(ValueError("foreign")))
        scanner = Scanner()
        scanner.port = relay.endpoint[1]
        try:
            with self.assertRaises((OSError, ValueError)):
                scanner.command(b"PING")
        finally:
            self.assertTrue(relay.close()["cleanupVerified"])

    def test_idle_client_socket_and_worker_have_finite_cleanup(self):
        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        relay = LoopbackRelay(lambda: listener.getsockname())
        client = socket.create_connection(relay.endpoint)
        backend, _ = listener.accept()
        started = time.monotonic()
        receipt = relay.close()
        client.close()
        backend.close()
        listener.close()
        self.assertTrue(receipt["cleanupVerified"])
        self.assertLess(time.monotonic()-started, 1)

    def test_scanner_endpoint_requires_matching_owned_internal_network_and_generation(self):
        scanner = Scanner("endpoint-test")
        scanner.container_id = "a"*64
        scanner.image_id = "sha256:"+"b"*64
        scanner.network_id = "c"*64
        scanner.receipt["containerGeneration"] = {"createdUtc": "created", "startedUtc": "started"}
        attached = {"NetworkID": scanner.network_id, "IPAddress": "172.31.0.2", "EndpointID": "d"*64}
        container = {"Id": scanner.container_id, "Image": scanner.image_id, "Created": "created",
                     "State": {"Running": True, "Paused": False, "Restarting": False, "StartedAt": "started"},
                     "Config": {"Labels": {"financial.acceptance.run": scanner.run_id}},
                     "HostConfig": {"NetworkMode": scanner.network_id, "PortBindings": {}, "PublishAllPorts": False,
                                    "Memory": 1610612736, "NanoCpus": 2000000000, "CapDrop": ["ALL"],
                                    "CapAdd": None, "ReadonlyRootfs": True},
                     "NetworkSettings": {"Networks": {"owned": attached}, "Ports": {"3310/tcp": None}}}
        network = {"Id": scanner.network_id, "Internal": True, "Driver": "bridge", "Labels": {"financial.acceptance.run": scanner.run_id},
                   "Containers": {scanner.container_id: {"IPv4Address": "172.31.0.2/16", "EndpointID": "d"*64}},
                   "IPAM": {"Config": [{"Subnet": "172.31.0.0/16"}]}}
        with patch.object(scanner, "docker", side_effect=[json.dumps([container]), json.dumps([network])]):
            self.assertEqual(("172.31.0.2", 3310), scanner.validate_backend_endpoint())
        for mutate in (lambda: network.update(Internal=False), lambda: attached.update(IPAddress="172.31.0.3"), lambda: container["State"].update(StartedAt="foreign")):
            mutate()
            with patch.object(scanner, "docker", side_effect=[json.dumps([container]), json.dumps([network])]):
                with self.assertRaises(ValueError):
                    scanner.validate_backend_endpoint()


if __name__ == "__main__":
    unittest.main()
