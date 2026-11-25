"""Regression checks for the Windows desktop packaging boundary."""
from __future__ import annotations

import tomllib
import unittest
from pathlib import Path

PACKAGE = Path(__file__).resolve().parents[1]


class WindowsPackagingTests(unittest.TestCase):
    def test_builder_owns_the_python_runtime_step(self) -> None:
        script = (PACKAGE / "packaging" / "windows" / "build-desktop.ps1").read_text(encoding="utf-8")
        self.assertIn("build-backend-runtime.ps1", script)
        self.assertIn("dist\\windows\\desktop", script)
        self.assertIn("SpiritScout-$version-windows-x64.zip", script)
        self.assertNotIn("build-portable.ps1", script)
        self.assertNotIn("SpiritScoutDesktop.cs", script)

    def test_removed_webview_shell_is_not_part_of_the_source_tree(self) -> None:
        removed = (
            "launcher",
            "desktop/SpiritScoutCompose/build.gradle.kts",
            "desktop/SpiritScoutCompose/settings.gradle.kts",
        )
        for relative in removed:
            self.assertFalse((PACKAGE / relative).exists(), relative)

    def test_compose_desktop_entrypoint_is_present(self) -> None:
        self.assertTrue(
            (PACKAGE / "desktop" / "src" / "main" / "kotlin"
             / "com" / "spiritscout" / "desktop" / "Main.kt").is_file()
        )
        self.assertTrue(
            (PACKAGE / "desktop" / "src" / "main" / "resources" / "favicon.png").is_file()
        )
        self.assertTrue((PACKAGE / "packaging" / "windows" / "icon.ico").is_file())

    def test_desktop_runtime_includes_websocket_support(self) -> None:
        project = tomllib.loads((PACKAGE / "pyproject.toml").read_text(encoding="utf-8"))
        dependencies = project["project"]["dependencies"]
        self.assertTrue(any(item.lower().startswith("websockets") for item in dependencies))


if __name__ == "__main__":
    unittest.main()
