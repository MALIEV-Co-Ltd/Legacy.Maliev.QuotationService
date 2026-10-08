"""Qualify held source bytes before executing actual hosted helper controls."""
import hashlib
import json
import os
from pathlib import Path
import stat
import sys
import types
import unittest

INVENTORY_BYTES = 2807
INVENTORY_SHA = "f9067b75d04c24fc3358f5c6f4dfa7edb91677b238782865661c6335fe2df890"
PROGRAM = "tools/InvoiceCompletionProducerAcceptance/companion/scanner_helper_hosted_controls.py"
PATHS = {
    "tools/InvoiceCompletionProducerAcceptance/companion/hosted_scanner_readiness.py",
    "tools/InvoiceCompletionProducerAcceptance/companion/scanner_docker_command.py",
    "tools/InvoiceCompletionProducerAcceptance/companion/test_scanner_docker_command.py",
    "docs/scanner-helper-hosted-controls.md",
    PROGRAM,
    "tools/InvoiceCompletionProducerAcceptance/companion/test_scanner_helper_hosted_source.py",
}


def require(value):
    if not value:
        raise ValueError("Source qualification refused")


def held_read(path, count, digest):
    require(type(count) is int and 0 < count <= 262144)
    require(type(digest) is str and len(digest) == 64
            and all(value in "0123456789abcdef" for value in digest))
    require(not any(parent.is_symlink() for parent in (path, *path.parents)))
    descriptor = os.open(path, os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW)
    try:
        before = os.fstat(descriptor)
        require(stat.S_ISREG(before.st_mode) and before.st_size == count)
        data = bytearray()
        while len(data) <= count:
            chunk = os.read(descriptor, min(4096, count + 1 - len(data)))
            if not chunk:
                break
            data.extend(chunk)
        after = os.fstat(descriptor)
        identity = lambda row: (row.st_dev, row.st_ino, row.st_size, row.st_mtime_ns, row.st_ctime_ns)
        require(identity(before) == identity(after) and len(data) == count
                and hashlib.sha256(data).hexdigest() == digest)
        return bytes(data)
    finally:
        os.close(descriptor)


def unique_fields(rows):
    result = {}
    for key, value in rows:
        require(key not in result)
        result[key] = value
    return result


def main():
    try:
        require(sys.platform.startswith("linux") and os.environ.get("GITHUB_ACTIONS") == "true"
                and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted")
        root = Path.cwd().resolve()
        require(Path(__file__).resolve().parent == root / "tools/InvoiceCompletionProducerAcceptance/companion")
        inventory = root / "tools/InvoiceCompletionProducerAcceptance/companion/scanner-helper-hosted-inventory.json"
        document = json.loads(held_read(inventory, INVENTORY_BYTES, INVENTORY_SHA),
                              object_pairs_hook=unique_fields)
        require(document["SchemaVersion"] == 1 and type(document["Files"]) is list
                and len(document["Files"]) == len(PATHS))
        held = {}
        for row in document["Files"]:
            require(type(row) is dict and set(row) == {"path", "bytes", "sha256"}
                    and row["path"] in PATHS and row["path"] not in held)
            held[row["path"]] = held_read(root / row["path"], row["bytes"], row["sha256"])
        require(set(held) == PATHS)
        def load_module(relative, name):
            module = types.ModuleType(name)
            module.__file__ = str(root / relative)
            sys.modules[name] = module
            exec(compile(held[relative], str(root / relative), "exec"), module.__dict__)
            return module

        if sys.argv[1:] == ["--models"]:
            prefix = "tools/InvoiceCompletionProducerAcceptance/companion/"
            load_module(prefix + "scanner_docker_command.py", "scanner_docker_command")
            load_module(PROGRAM, "scanner_helper_hosted_controls")
            tests = [load_module(prefix + name + ".py", name) for name in
                     ("test_scanner_docker_command", "test_scanner_helper_hosted_source")]
            suite = unittest.TestSuite(unittest.defaultTestLoader.loadTestsFromModule(module)
                                       for module in tests)
            require(suite.countTestCases() == 34)
            result = unittest.TextTestRunner(verbosity=1).run(suite)
            return 0 if result.wasSuccessful() else 1
        program = load_module(PROGRAM, "qualified_scanner_helper_controls")
    except BaseException:
        print("SCANNER_HELPER_SOURCE_QUALIFICATION_FAILED")
        return 1
    return program.main()


if __name__ == "__main__":
    raise SystemExit(main())
