package com.spiritscout.desktop.service

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import kotlin.io.path.exists
import kotlin.io.path.isRegularFile

data class RuntimeLocation(
    val baseDirectory: Path,
    val python: Path,
    val displayName: String
)

object RuntimeResolver {
    suspend fun resolve(onProgress: (String) -> Unit): RuntimeLocation {
        onProgress("正在确认运行环境…")

        val executable = currentExecutable()
        val executableDir = executable?.parent
        if (executableDir != null) {
            val bundled = executableDir.resolve("py/python.exe")
            if (bundled.isRegularFile() && executableDir.resolve("configs/system.yaml").exists()) {
                return RuntimeLocation(executableDir, bundled, "便携运行时")
            }
        }

        val repository = findRepositoryRoot(Paths.get("").toAbsolutePath())
        if (repository != null) {
            val python = repository.resolve(".venv/Scripts/python.exe")
            if (python.isRegularFile()) {
                return RuntimeLocation(repository, python, "开发环境")
            }
        }

        val pathPython = findOnPath("python.exe")
        if (pathPython != null) {
            return RuntimeLocation(repository ?: Paths.get("").toAbsolutePath(), pathPython, "系统 Python")
        }

        error("没有找到可用的 SpiritScout Python 运行时。")
    }

    private fun currentExecutable(): Path? {
        val command = ProcessHandle.current().info().command().orElse(null) ?: return null
        return runCatching { Paths.get(command).toAbsolutePath() }.getOrNull()
    }

    private fun findRepositoryRoot(start: Path): Path? {
        var current: Path? = start
        repeat(10) {
            val candidate = current ?: return null
            if (candidate.resolve("pyproject.toml").isRegularFile()
                && candidate.resolve("configs/system.yaml").isRegularFile()
            ) {
                return candidate
            }
            current = candidate.parent
        }
        return null
    }

    private fun findOnPath(executable: String): Path? {
        val path = System.getenv("PATH").orEmpty()
        return path.split(java.io.File.pathSeparator)
            .asSequence()
            .mapNotNull { runCatching { Paths.get(it, executable) }.getOrNull() }
            .firstOrNull { Files.isRegularFile(it) }
    }
}
