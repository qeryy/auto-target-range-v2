package com.spiritscout.desktop.service

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import java.net.ServerSocket
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter
import java.util.concurrent.TimeUnit

class BackendHost {
    var baseUrl: String = ""
        private set
    var port: Int = 0
        private set
    var logPath: Path? = null
        private set
    var runtime: RuntimeLocation? = null
        private set

    private var process: Process? = null
    private val recentLines = ArrayDeque<String>()

    val isRunning: Boolean
        get() = process?.isAlive == true

    suspend fun start(runtime: RuntimeLocation, onLog: (String) -> Unit): BackendHost {
        this.runtime = runtime
        port = ServerSocket(0).use { it.localPort }
        baseUrl = "http://127.0.0.1:$port"

        val logRoot = Path.of(System.getenv("LOCALAPPDATA") ?: System.getProperty("java.io.tmpdir"))
            .resolve("SpiritScout/desktop-logs")
        Files.createDirectories(logRoot)
        logPath = logRoot.resolve("desktop-${LocalDate.now()}.log")

        val command = listOf(
            runtime.python.toString(),
            "-X", "utf8",
            "-m", "spiritscout",
            "--config", "configs/system.yaml",
            "--host", "127.0.0.1",
            "--port", port.toString()
        )
        val builder = ProcessBuilder(command)
            .directory(runtime.baseDirectory.toFile())
            .redirectErrorStream(false)
        builder.environment()["PYTHONUNBUFFERED"] = "1"
        builder.environment()["PYTHONIOENCODING"] = "utf-8"
        builder.environment()["SPIRITSCOUT_DESKTOP"] = "1"
        val started = builder.start()
        process = started

        capture(started.inputStream.bufferedReader(StandardCharsets.UTF_8), "stdout", onLog)
        capture(started.errorStream.bufferedReader(StandardCharsets.UTF_8), "stderr", onLog)
        appendLog("桌面端启动 | ${runtime.displayName} | $baseUrl", onLog)

        val client = HttpClient.newBuilder()
            .version(HttpClient.Version.HTTP_1_1)
            .connectTimeout(java.time.Duration.ofSeconds(3))
            .build()
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(120)
        while (System.nanoTime() < deadline) {
            if (!started.isAlive) {
                error("后端启动后退出（退出码 ${started.exitValue()}）。\n${tailLog()}")
            }
            val ok = runCatching {
                val request = HttpRequest.newBuilder(URI.create("$baseUrl/login"))
                    .timeout(java.time.Duration.ofSeconds(4))
                    .GET()
                    .build()
                client.send(request, HttpResponse.BodyHandlers.discarding()).statusCode() in 200..399
            }.getOrDefault(false)
            if (ok) {
                appendLog("后端已就绪", onLog)
                return this
            }
            delay(350)
        }
        error("等待后端就绪超时。\n${tailLog()}")
    }

    private fun capture(reader: java.io.Reader, stream: String, onLog: (String) -> Unit) {
        Thread({
            try {
                reader.useLines { lines ->
                    lines.forEach { line -> appendLog("[$stream] $line", onLog) }
                }
            } catch (_: Exception) {
                // The stream closes as part of process shutdown.
            }
        }, "spiritscout-$stream-reader").apply {
            isDaemon = true
            start()
        }
    }

    private fun appendLog(message: String, onLog: (String) -> Unit) {
        val line = "[${LocalDateTime.now().format(DateTimeFormatter.ofPattern("HH:mm:ss"))}] $message"
        synchronized(recentLines) {
            recentLines.addLast(line)
            while (recentLines.size > 2000) recentLines.removeAt(0)
        }
        logPath?.let { path ->
            runCatching {
                Files.writeString(
                    path,
                    line + System.lineSeparator(),
                    StandardCharsets.UTF_8,
                    java.nio.file.StandardOpenOption.CREATE,
                    java.nio.file.StandardOpenOption.APPEND
                )
            }
        }
        onLog(line)
    }

    fun tailLog(lines: Int = 30): String = synchronized(recentLines) {
        recentLines.toList().takeLast(lines).joinToString("\n")
    }

    suspend fun stop() {
        val current = process ?: return
        withContext(Dispatchers.IO) {
            runCatching {
                current.descendants().forEach { it.destroyForcibly() }
                current.destroy()
                if (!current.waitFor(4, TimeUnit.SECONDS)) {
                    current.destroyForcibly()
                    current.waitFor(4, TimeUnit.SECONDS)
                }
            }
        }
        process = null
    }
}
