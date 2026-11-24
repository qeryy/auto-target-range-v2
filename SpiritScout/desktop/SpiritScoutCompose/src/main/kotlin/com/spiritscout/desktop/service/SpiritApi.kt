package com.spiritscout.desktop.service

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.net.CookieManager
import java.net.CookiePolicy
import java.net.URI
import java.net.URLEncoder
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.time.Duration

class ApiException(
    val status: Int,
    message: String,
    val body: String
) : Exception(message)

class SpiritApi(val baseUrl: String) {
    private val json = Json {
        ignoreUnknownKeys = true
        explicitNulls = false
    }
    private val cookies = CookieManager(null, CookiePolicy.ACCEPT_ALL)
    private val client = HttpClient.newBuilder()
        .version(HttpClient.Version.HTTP_1_1)
        .cookieHandler(cookies)
        .connectTimeout(Duration.ofSeconds(8))
        .followRedirects(HttpClient.Redirect.NORMAL)
        .build()

    suspend fun get(path: String): JsonElement = request("GET", path, null)

    suspend fun post(path: String, body: String = "{}"): JsonElement =
        request("POST", path, body, "application/json")

    suspend fun put(path: String, body: String): JsonElement =
        request("PUT", path, body, "application/json")

    suspend fun patch(path: String, body: String): JsonElement =
        request("PATCH", path, body, "application/json")

    suspend fun delete(path: String): JsonElement = request("DELETE", path, null)

    suspend fun postMessage(
        sessionId: String,
        content: String,
        uploadIds: List<String> = emptyList()
    ): JsonElement {
        val payload = buildJsonObject {
            put("content", content)
            put("message_id", "desktop-${System.currentTimeMillis()}-${(1000..9999).random()}")
            put(
                "upload_ids",
                kotlinx.serialization.json.JsonArray(
                    uploadIds.map { kotlinx.serialization.json.JsonPrimitive(it) }
                )
            )
        }
        return post("/api/sessions/${enc(sessionId)}/messages", payload.toString())
    }

    suspend fun upload(path: Path): String {
        val name = path.fileName.toString()
        val uri = URI.create("$baseUrl/api/uploads?name=${enc(name)}")
        val request = HttpRequest.newBuilder(uri)
            .timeout(Duration.ofMinutes(3))
            .header("Content-Type", "application/octet-stream")
            .POST(HttpRequest.BodyPublishers.ofFile(path))
            .build()
        val response = client.send(request, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
        val body = response.body()
        ensureSuccess(response.statusCode(), body)
        return json.parseToJsonElement(body).jsonObject["upload_id"]?.toString()?.trim('"')
            ?: error("上传响应缺少 upload_id")
    }

    suspend fun postRawAsync(path: String, stream: java.io.InputStream, contentType: String): JsonElement =
        withContext(Dispatchers.IO) {
            val request = HttpRequest.newBuilder(URI.create(baseUrl.trimEnd('/') + "/" + path.trimStart('/')))
                .timeout(Duration.ofMinutes(3))
                .header("Content-Type", contentType)
                .header("Accept", "application/json")
                .POST(HttpRequest.BodyPublishers.ofInputStream { stream })
                .build()
            val response = client.send(request, HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
            val body = response.body()
            ensureSuccess(response.statusCode(), body)
            if (body.isBlank()) json.parseToJsonElement("{}") else json.parseToJsonElement(body)
        }

    suspend fun login(username: String, password: String): Boolean {
        val body = buildJsonObject {
            put("username", username)
            put("password", password)
        }
        return try {
            post("/api/auth/login", body.toString())
            true
        } catch (error: ApiException) {
            if (error.status == 401 || error.status == 403) false else throw error
        }
    }

    private suspend fun request(
        method: String,
        path: String,
        body: String?,
        contentType: String? = null
    ): JsonElement = withContext(Dispatchers.IO) {
        val builder = HttpRequest.newBuilder(URI.create(baseUrl.trimEnd('/') + "/" + path.trimStart('/')))
            .timeout(Duration.ofSeconds(60))
            .header("Accept", "application/json")
        contentType?.let { builder.header("Content-Type", it) }
        val publisher = if (body == null) {
            HttpRequest.BodyPublishers.noBody()
        } else {
            HttpRequest.BodyPublishers.ofString(body, StandardCharsets.UTF_8)
        }
        builder.method(method, publisher)

        val response = client.send(builder.build(), HttpResponse.BodyHandlers.ofString(StandardCharsets.UTF_8))
        val text = response.body()
        ensureSuccess(response.statusCode(), text)
        if (text.isBlank()) json.parseToJsonElement("{}") else json.parseToJsonElement(text)
    }

    private fun ensureSuccess(status: Int, body: String) {
        if (status in 200..299) return
        val message = runCatching {
            val root = json.parseToJsonElement(body)
            root.jsonObject["detail"]?.toString()?.trim('"') ?: body
        }.getOrDefault(body)
        throw ApiException(status, message.ifBlank { "HTTP $status" }, body)
    }

    private fun enc(value: String) = URLEncoder.encode(value, StandardCharsets.UTF_8)
}

private val JsonElement.jsonObject get() = this as kotlinx.serialization.json.JsonObject
