"""Check pinned patch compatibility without starting the bridge or touching HID."""
import collections
import importlib.util
from pathlib import Path
import re
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts'))
import backend_diagnostics_patch as diagnostics
import backend_runtime_patch as runtime

spec = importlib.util.spec_from_file_location('managed_patch', ROOT / 'scripts/patch-backend.py')
managed = importlib.util.module_from_spec(spec)
spec.loader.exec_module(managed)
UPSTREAM = ROOT / '.local/upstream' / ('vds-' + managed.COMMIT)


def literals(text):
    return collections.Counter(re.findall(r'"(?:\\.|[^"\\])*"', text))


class DiagnosticPatchTests(unittest.TestCase):
    def test_snapshot_keeps_metric_names(self):
        expression = '" dropped_delta=" + std::to_string(dropped_delta) + " pending=" + std::to_string(state.pending_audio_chunks.size());'
        _, _, rewritten = diagnostics.state_snapshot(expression, 'snapshot', [('dropped_delta', 'std::uint64_t')])
        self.assertEqual(literals(expression), literals(rewritten))
        self.assertIn('std::to_string(snapshot.dropped_delta)', rewritten)
        self.assertIn('std::to_string(snapshot.pending)', rewritten)

    def test_pinned_daemon_log_schema_unchanged(self):
        old = runtime.daemon(managed.patch_daemon((UPSTREAM / 'src/platform/win32/vdsd.cc').read_text(encoding='utf-8')))
        new = diagnostics.daemon(old)
        self.assertEqual(literals(old), literals(new))
        self.assertIn('logger.log(vds::LogScope::Output, vds::LogLevel::Info,\n               "shutdown neutral reports written;', new)

    def test_transport_diagnostic_schema_unchanged(self):
        old = runtime.transport((UPSTREAM / 'src/platform/win32/vds_bt.cc').read_text(encoding='utf-8'))
        self.assertEqual(literals(old), literals(diagnostics.transport(old)))


if __name__ == '__main__':
    unittest.main()
