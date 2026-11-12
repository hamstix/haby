#!/usr/bin/env python3
"""Controlled delayed readiness publication; no PostgreSQL backend is started."""
import argparse
import importlib.util
import json
import os
import pathlib
import signal
import subprocess
import sys
import tempfile
import time
import unittest

sys.dont_write_bytecode = True


def wait_until(predicate, seconds=8):
    deadline = time.monotonic() + seconds
    while not predicate():
        if time.monotonic() >= deadline:
            raise TimeoutError('Owned publication control deadline expired')
        time.sleep(0.02)


def publication_child(directory):
    root = pathlib.Path(directory)
    spec = importlib.util.spec_from_file_location('owned_relay', pathlib.Path(__file__).with_name('postgresql-loopback-relay.py'))
    relay = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(relay)
    original = pathlib.Path.write_text

    def delayed_write(path, data, *args, **kwargs):
        if path.parent == root and path.name.startswith('ready.json'):
            # Hold the actual publisher halfway through its write, before close.
            with path.open('w', encoding='utf-8') as stream:
                split = len(data) // 2
                stream.write(data[:split])
                stream.flush()
                original(root / 'partial.signal', path.name)
                wait_until(lambda: (root / 'release.signal').exists())
                if (root / 'abort.signal').exists():
                    raise OSError('Controlled publication failure')
                stream.write(data[split:])
            return len(data)
        return original(path, data, *args, **kwargs)

    pathlib.Path.write_text = delayed_write
    args = argparse.Namespace(ready=str(root / 'ready.json'), events=str(root / 'events.jsonl'),
                              run_id='controlled-run', fixture_id='controlled-fixture',
                              upstream_host='127.0.0.1', upstream_port=9, connect_timeout_seconds=5)
    relay.asyncio.run(relay.main(args))


class PublicationTests(unittest.TestCase):
    def test_failed_publication_removes_only_its_owned_temporary_file(self):
        with tempfile.TemporaryDirectory(prefix='haby-relay-publication-failure-') as directory:
            root = pathlib.Path(directory)
            (root / 'unrelated.keep').write_text('unchanged')
            child = subprocess.Popen([sys.executable, __file__, '--publication-child', directory],
                                     stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                wait_until(lambda: (root / 'partial.signal').exists())
                publication = (root / 'partial.signal').read_text()
                (root / 'abort.signal').touch()
                (root / 'release.signal').touch()
                stdout, stderr = child.communicate(timeout=5)
                print('FAILED_PUBLICATION_CONTROL ' + json.dumps(dict(pid=child.pid,
                      exitCode=child.returncode, publicationPath=publication,
                      finalVisible=(root / 'ready.json').exists(),
                      publicationStillPresent=(root / publication).exists())), flush=True)
                sys.stdout.write(stdout.decode(errors='replace'))
                sys.stderr.write(stderr.decode(errors='replace'))
                self.assertNotEqual(0, child.returncode)
                self.assertFalse((root / 'ready.json').exists())
                self.assertFalse((root / publication).exists())
                self.assertEqual('unchanged', (root / 'unrelated.keep').read_text())
            finally:
                if child.poll() is None:
                    child.kill()
                child.communicate(timeout=2)

    def test_partial_write_is_not_a_published_ready_file(self):
        with tempfile.TemporaryDirectory(prefix='haby-relay-publication-') as directory:
            root = pathlib.Path(directory)
            child = subprocess.Popen([sys.executable, __file__, '--publication-child', directory],
                                     stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                wait_until(lambda: (root / 'partial.signal').exists())
                print('READINESS_PARTIAL_CONTROL ' + json.dumps(dict(pid=child.pid,
                      publicationPath=(root / 'partial.signal').read_text(),
                      finalVisible=(root / 'ready.json').exists())), flush=True)
                self.assertFalse((root / 'ready.json').exists(), 'Partial JSON became visible as final readiness')
                (root / 'release.signal').touch()
                wait_until(lambda: (root / 'ready.json').exists())
                ready = json.loads((root / 'ready.json').read_text())
                self.assertEqual('controlled-run', ready['runId'])
                self.assertEqual('controlled-fixture', ready['fixtureId'])
                self.assertEqual(child.pid, ready['pid'])
                self.assertTrue(1 <= ready['port'] <= 65535)
                print('ATOMIC_READINESS_CONTROL pid=%d runId=%s fixtureId=%s port=%d' %
                      (child.pid, ready['runId'], ready['fixtureId'], ready['port']), flush=True)
            finally:
                (root / 'release.signal').touch()
                # Linux is the only supported relay venue. Wait for this exact
                # child's SIGTERM handler before requesting bounded shutdown.
                try:
                    wait_until(lambda: bool(int(next(line.split()[1] for line in pathlib.Path(
                        '/proc/%d/status' % child.pid).read_text().splitlines() if line.startswith('SigCgt:')), 16)
                        & (1 << (signal.SIGTERM - 1))))
                except (TimeoutError, FileNotFoundError):
                    pass
                child.terminate()
                try:
                    stdout, stderr = child.communicate(timeout=5)
                except subprocess.TimeoutExpired:
                    child.kill()
                    stdout, stderr = child.communicate(timeout=2)
                sys.stdout.write(stdout.decode(errors='replace'))
                sys.stderr.write(stderr.decode(errors='replace'))


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == '--publication-child':
        publication_child(sys.argv[2])
    else:
        if not sys.platform.startswith('linux'):
            raise SystemExit('This controlled relay check requires actual Linux.')
        unittest.main()
