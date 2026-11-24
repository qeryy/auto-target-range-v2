from __future__ import annotations

import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import yaml

from spiritscout.config import load_system_config


class LegacySystemConfigTests(unittest.TestCase):
    def _load(self, data: dict, env: dict[str, str] | None = None):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "system.yaml"
            path.write_text(
                yaml.safe_dump(data, allow_unicode=True, sort_keys=False),
                encoding="utf-8",
            )
            with patch.dict(os.environ, env or {}, clear=False):
                return load_system_config(path)

    def test_old_desktop_fields_are_migrated(self) -> None:
        cfg = self._load({
            "executor": {"sandbox_backend": "host"},
            "web": {"auth_mode": "none"},
            "experience": {
                "distill_enabled": False,
                "sync_repo": "",
                "sync_branch": "main",
                "sync_path": "experience-export/experience-insights.json",
                "sync_inbox": "experience-export/inbox",
                "sync_user": "",
            },
        })
        self.assertEqual(cfg.executor.backend, "local")
        self.assertFalse(cfg.experience.auto_distill)

    def test_old_docker_backend_is_preserved(self) -> None:
        cfg = self._load({"executor": {"sandbox_backend": "docker"}})
        self.assertEqual(cfg.executor.backend, "docker")

    def test_old_environment_override_no_longer_breaks_startup(self) -> None:
        cfg = self._load(
            {"workspace": {"root": ".spiritscout"}},
            {
                "SPIRITSCOUT_SANDBOX_BACKEND": "host",
                "SPIRITSCOUT_EXECUTOR__SANDBOX_BACKEND": "host",
            },
        )
        self.assertEqual(cfg.executor.backend, "local")


if __name__ == "__main__":
    unittest.main()
