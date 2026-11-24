from __future__ import annotations

import os
import unittest

from spiritscout.web.system_resources import ResourceMonitor


@unittest.skipUnless(os.name == "nt", "Windows resource sampling test")
class WindowsResourceTests(unittest.TestCase):
    def test_snapshot_samples_system_and_application_resources(self) -> None:
        monitor = ResourceMonitor()
        first = monitor.snapshot()
        self.assertTrue(first["available"], first.get("detail"))
        self.assertGreater(first["system"]["memory_total"], 0)
        self.assertIsNotNone(first["application"])
        self.assertGreater(first["application"]["process_count"], 0)


if __name__ == "__main__":
    unittest.main()
