package com.spiritscout.desktop

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.spiritscout.desktop.model.MetricItem
import com.spiritscout.desktop.model.MetricTone
import com.spiritscout.desktop.model.NewTaskRequest
import com.spiritscout.desktop.model.PendingApprovalItem
import com.spiritscout.desktop.model.PendingQuestionItem
import com.spiritscout.desktop.model.JsonActionState
import com.spiritscout.desktop.model.ResourceItem
import com.spiritscout.desktop.model.ResourcePaging
import com.spiritscout.desktop.model.Section
import com.spiritscout.desktop.model.SessionItem
import com.spiritscout.desktop.model.SystemResourceSnapshot
import com.spiritscout.desktop.model.TimelineItem
import com.spiritscout.desktop.model.TimelineKind
import com.spiritscout.desktop.model.WorkTask
import com.spiritscout.desktop.model.WorkspaceSnapshot
import com.spiritscout.desktop.model.WorkspaceTab
import com.spiritscout.desktop.model.array
import com.spiritscout.desktop.model.boolean
import com.spiritscout.desktop.model.double
import com.spiritscout.desktop.model.element
import com.spiritscout.desktop.model.int
import com.spiritscout.desktop.model.long
import com.spiritscout.desktop.model.objectOrEmpty
import com.spiritscout.desktop.model.string
import com.spiritscout.desktop.service.ApiException
import com.spiritscout.desktop.service.BackendHost
import com.spiritscout.desktop.service.RuntimeResolver
import com.spiritscout.desktop.service.SpiritApi
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.swing.Swing
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.jsonObject
import java.nio.file.Files
import java.nio.file.Path
import java.net.URLEncoder
import java.net.URI
import java.nio.charset.StandardCharsets
import java.time.Instant

data class PendingUpload(val id: String, val name: String)

class AppController {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Swing)
    private val backend = BackendHost()
    private var api: SpiritApi? = null

    var booting by mutableStateOf(true)
        private set
    var bootMessage by mutableStateOf("正在启动灵枢…")
        private set
    var bootError by mutableStateOf<String?>(null)
        private set
    var backendUrl by mutableStateOf("")
        private set
    var runtimeName by mutableStateOf("")
        private set
    var requiresLogin by mutableStateOf(false)
        private set
    var loginError by mutableStateOf<String?>(null)
        private set
    var darkTheme by mutableStateOf(false)
        private set

    var section by mutableStateOf(Section.DASHBOARD)
        private set
    var selectedSession by mutableStateOf<SessionItem?>(null)
        private set
    var workspace by mutableStateOf<WorkspaceSnapshot?>(null)
        private set
    var workspaceLoading by mutableStateOf(false)
        private set
    var workspaceDraft by mutableStateOf("")
        private set
    var workspaceError by mutableStateOf<String?>(null)
        private set
    var workspaceTab by mutableStateOf(WorkspaceTab.CHAT)
        private set
    var workspaceTabJson by mutableStateOf<JsonElement?>(null)
        private set
    var workspaceTabLoading by mutableStateOf(false)
        private set
    var workspaceTabError by mutableStateOf<String?>(null)
        private set

    val sessions = mutableStateListOf<SessionItem>()
    val metrics = mutableStateListOf<MetricItem>()
    val resources = mutableStateMapOf<Section, List<ResourceItem>>()
    val loading = mutableStateMapOf<Section, Boolean>()
    val errors = mutableStateMapOf<Section, String?>()
    val logs = mutableStateListOf<String>()
    val models = mutableStateListOf<String>()
    val pendingUploads = mutableStateListOf<PendingUpload>()

    var system by mutableStateOf<SystemResourceSnapshot?>(null)
        private set
    var settingsJson by mutableStateOf<JsonElement?>(null)
        private set
    var dashboardPeriod by mutableStateOf("today")
        private set
    var showNewTask by mutableStateOf(false)
        private set
    var taskError by mutableStateOf<String?>(null)
        private set
    var uploading by mutableStateOf(false)
        private set
    var toast by mutableStateOf<String?>(null)
        private set
    var jsonAction by mutableStateOf<JsonActionState?>(null)
        private set
    var jsonActionError by mutableStateOf<String?>(null)
        private set
    var showApiConsole by mutableStateOf(false)
        private set
    var apiConsoleLoading by mutableStateOf(false)
        private set
    var apiConsoleResponse by mutableStateOf("")
        private set
    var apiConsoleError by mutableStateOf<String?>(null)
        private set

    fun bootstrap() {
        scope.launch {
            try {
                booting = true
                bootError = null
                val runtime = withContext(Dispatchers.IO) {
                    RuntimeResolver.resolve { message ->
                        scope.launch { bootMessage = message }
                    }
                }
                runtimeName = runtime.displayName
                bootMessage = "正在启动本机控制台…"
                withContext(Dispatchers.IO) {
                    backend.start(runtime) { line ->
                        scope.launch {
                            logs.add(line)
                            while (logs.size > 1200) logs.removeAt(0)
                        }
                    }
                }
                backendUrl = backend.baseUrl
                api = SpiritApi(backend.baseUrl)
                bootMessage = "正在加载工作区…"
                try {
                    api?.get("/api/sessions")
                } catch (error: ApiException) {
                    if (error.status == 401 || error.status == 403) {
                        requiresLogin = true
                        bootMessage = "需要管理员登录"
                        booting = false
                        return@launch
                    }
                    throw error
                }
                loadDashboardInternal()
                loadSessionsInternal()
                booting = false
            } catch (error: Exception) {
                bootError = error.message ?: error.toString()
                booting = false
                logs.add("启动失败：${error.stackTraceToString()}")
            }
        }
    }

    fun navigate(next: Section) {
        section = next
        selectedSession = null
        workspace = null
        workspaceTab = WorkspaceTab.CHAT
        workspaceTabJson = null
        resources[next]?.let { return }
        scope.launch { loadSection(next) }
    }

    fun refreshCurrent() {
        val target = section
        scope.launch {
            when (target) {
                Section.DASHBOARD -> loadDashboardInternal()
                Section.TASKS -> loadSessionsInternal()
                Section.SETTINGS -> loadSystemInternal()
                Section.WORKSPACE -> refreshWorkspaceInternal()
                else -> loadSection(target)
            }
        }
    }

    fun selectWorkspaceTab(tab: WorkspaceTab) {
        workspaceTab = tab
        workspaceTabJson = null
        workspaceTabError = null
        if (tab == WorkspaceTab.CHAT) return
        scope.launch { loadWorkspaceTabInternal(tab) }
    }

    fun openSession(session: SessionItem) {
        selectedSession = session
        section = Section.WORKSPACE
        workspace = null
        workspaceTab = WorkspaceTab.CHAT
        workspaceTabJson = null
        workspaceError = null
        scope.launch { refreshWorkspaceInternal() }
    }

    fun returnToTasks() {
        section = Section.TASKS
        selectedSession = null
        workspace = null
        scope.launch { loadSessionsInternal() }
    }

    fun openNewTask() {
        taskError = null
        pendingUploads.clear()
        showNewTask = true
        scope.launch { loadModelsInternal() }
    }

    fun closeNewTask() {
        showNewTask = false
        taskError = null
    }

    fun setDraft(value: String) {
        workspaceDraft = value
    }

    fun sendWorkspaceMessage() {
        val content = workspaceDraft.trim()
        val currentApi = api ?: return
        val current = selectedSession ?: return
        if (content.isBlank() && pendingUploads.isEmpty()) return
        workspaceDraft = ""
        scope.launch {
            try {
                currentApi.postMessage(current.id, content, pendingUploads.map { it.id })
                pendingUploads.clear()
                workspace = workspace?.copy(
                    timeline = workspace!!.timeline + TimelineItem("你", content, TimelineKind.USER)
                )
                toast = "消息已发送"
                refreshWorkspaceInternal()
            } catch (error: Exception) {
                workspaceError = error.message
            }
        }
    }

    fun sessionAction(action: String) {
        val currentApi = api ?: return
        val current = selectedSession ?: return
        scope.launch {
            try {
                currentApi.post("/api/sessions/${current.id}/$action")
                refreshWorkspaceInternal()
                loadSessionsInternal()
            } catch (error: Exception) {
                workspaceError = error.message
            }
        }
    }

    fun changeSessionModel() {
        val current = selectedSession ?: return
        openJsonAction(
            JsonActionState(
                title = "切换会话模型",
                description = "填写模型名称；留空由后端规则处理。",
                initialJson = """{"model":"${current.model.takeIf { it != "自动" }.orEmpty()}"}""",
                method = "POST",
                pathTemplate = "/api/sessions/${url(current.id)}/model",
                reloadSection = Section.WORKSPACE,
                successMessage = "会话模型已更新"
            )
        )
    }

    fun openSessionManageAction(operation: String) {
        val current = selectedSession ?: return
        val sid = url(current.id)
        val action = when (operation) {
            "rename" -> JsonActionState(
                title = "重命名会话",
                description = "清空标题可恢复自动命名。",
                initialJson = prettyJson.encodeToString(
                    JsonElement.serializer(),
                    buildJsonObject { put("title", current.title) }
                ),
                method = "PATCH",
                pathTemplate = "/api/sessions/$sid",
                reloadSection = Section.WORKSPACE,
                successMessage = "会话已重命名"
            )
            "project" -> JsonActionState(
                title = "切换会话项目",
                description = "填写项目 ID；空字符串表示移到未分类。",
                initialJson = """{"project_id":""}""",
                method = "PATCH",
                pathTemplate = "/api/sessions/$sid",
                reloadSection = Section.WORKSPACE,
                successMessage = "会话项目已更新"
            )
            else -> JsonActionState(
                title = "删除会话",
                description = "确认删除会话记录、工作区和交付文件。",
                method = "DELETE",
                pathTemplate = "/api/sessions/$sid?confirm=true",
                reloadSection = Section.TASKS,
                navigateOnSuccess = true,
                successMessage = "会话已删除"
            )
        }
        openJsonAction(action)
    }

    fun decideApproval(approvalId: String, allow: Boolean) {
        val currentApi = api ?: return
        val current = selectedSession ?: return
        scope.launch {
            try {
                currentApi.post(
                    "/api/sessions/${url(current.id)}/approvals/${url(approvalId)}",
                    buildJsonObject { put("allow", allow) }.toString()
                )
                toast = if (allow) "已允许本次调用" else "已拒绝本次调用"
                refreshWorkspaceInternal()
            } catch (error: Exception) {
                workspaceError = error.message
            }
        }
    }

    fun answerQuestion(questionId: String, choice: Int? = null) {
        val currentApi = api ?: return
        val current = selectedSession ?: return
        if (choice != null) {
            scope.launch {
                try {
                    currentApi.post(
                        "/api/sessions/${url(current.id)}/questions/${url(questionId)}",
                        buildJsonObject { put("choice", choice) }.toString()
                    )
                    toast = "已提交答复"
                    refreshWorkspaceInternal()
                } catch (error: Exception) {
                    workspaceError = error.message
                }
            }
            return
        }
        openJsonAction(
            JsonActionState(
                title = "回答智能体提问",
                description = "可用 text 回答，或按题目要求提交 choices 数组。",
                initialJson = """{"text":""}""",
                method = "POST",
                pathTemplate = "/api/sessions/${url(current.id)}/questions/${url(questionId)}",
                reloadSection = Section.WORKSPACE,
                successMessage = "答复已提交"
            )
        )
    }

    fun stopBackgroundTask(jobId: String) {
        val currentApi = api ?: return
        val current = selectedSession ?: return
        scope.launch {
            try {
                currentApi.post(
                    "/api/sessions/${url(current.id)}/background-tasks/${url(jobId)}/stop"
                )
                toast = "后台任务已停止"
                loadWorkspaceTabInternal(WorkspaceTab.BACKGROUND)
            } catch (error: Exception) {
                workspaceTabError = error.message
            }
        }
    }

    fun openBackendUrl(path: String) {
        val url = backendUrl.trimEnd('/') + "/" + path.trimStart('/')
        runCatching { java.awt.Desktop.getDesktop().browse(URI.create(url)) }
            .onFailure { toast = "无法打开链接：${it.message}" }
    }

    fun openTrafficSession(item: ResourceItem) {
        val currentApi = api ?: return
        val sid = item.id
        if (sid.isBlank()) return
        scope.launch {
            try {
                val detail = currentApi.get("/api/sessions/${url(sid)}")
                openSession(SessionItem.fromJson(detail))
                selectWorkspaceTab(WorkspaceTab.TRAFFIC)
            } catch (error: Exception) {
                toast = error.message
            }
        }
    }

    fun createTask(request: NewTaskRequest, onCreated: (SessionItem) -> Unit) {
        val currentApi = api ?: return
        scope.launch {
            try {
                taskError = null
                val payload = buildJsonObject {
                    put("title", request.title)
                    put("mode", request.mode)
                    put("target", request.target)
                    put("mission", request.mission)
                    put("scope", kotlinx.serialization.json.JsonArray(request.scope.map { kotlinx.serialization.json.JsonPrimitive(it) }))
                    put("model", request.model)
                    put("generate_wp", request.generateWriteup)
                    put("multiple_answers", request.multipleAnswers)
                    put("allow_sensitive", request.allowSensitive)
                    put("authorized", true)
                    put("project_id", request.projectId)
                    put("proxy_plan_id", request.proxyPlanId.ifBlank { "direct" })
                    put("asset_strategy", request.assetStrategy)
                    put("excluded_assets", kotlinx.serialization.json.JsonArray(
                        request.excludedAssets.map { kotlinx.serialization.json.JsonPrimitive(it) }
                    ))
                    put("platform", request.platform)
                    put("flag_format", request.flagFormat)
                    put("ctf_type_hints", kotlinx.serialization.json.JsonArray(
                        request.ctfTypeHints.map { kotlinx.serialization.json.JsonPrimitive(it) }
                    ))
                    put("ctf_type_note", request.ctfTypeNote)
                    put("challenges_yaml", request.challengesYaml.ifBlank { null })
                    put("platform_yaml", request.platformYaml.ifBlank { null })
                    put("rules_yaml", request.rulesYaml.ifBlank { null })
                    put("contest_type", request.contestType)
                    put("budget_tokens", request.budgetTokens)
                    put("rps", request.rps)
                    put("no_llm", request.noLlm)
                    put("accept_rule_conflicts", request.acceptRuleConflicts)
                    put("browser_account_ids", kotlinx.serialization.json.JsonArray(
                        request.browserAccountIds.map { kotlinx.serialization.json.JsonPrimitive(it) }
                    ))
                    put("upload_ids", kotlinx.serialization.json.JsonArray(
                        (request.uploadIds + pendingUploads.map { it.id })
                            .distinct()
                            .map { kotlinx.serialization.json.JsonPrimitive(it) }
                    ))
                }
                val result = currentApi.post("/api/sessions", payload.toString())
                val created = SessionItem.fromJson(result)
                showNewTask = false
                loadSessionsInternal()
                onCreated(created)
            } catch (error: Exception) {
                taskError = error.message ?: "创建任务失败"
            }
        }
    }

    fun addUploads(paths: List<Path>) {
        val currentApi = api ?: return
        if (paths.isEmpty()) return
        scope.launch {
            uploading = true
            taskError = null
            try {
                paths.forEach { path ->
                    val id = currentApi.upload(path)
                    pendingUploads += PendingUpload(id, path.fileName.toString())
                }
            } catch (error: Exception) {
                taskError = "附件上传失败：${error.message}"
            } finally {
                uploading = false
            }
        }
    }

    fun removeUpload(id: String) {
        pendingUploads.removeAll { it.id == id }
    }

    fun clearUploads() {
        pendingUploads.clear()
    }

    fun clearToast() {
        toast = null
    }

    fun openApiConsole() {
        apiConsoleError = null
        apiConsoleResponse = ""
        showApiConsole = true
    }

    fun closeApiConsole() {
        showApiConsole = false
        apiConsoleError = null
    }

    fun executeApiConsole(method: String, path: String, body: String) {
        val currentApi = api ?: return
        if (path.isBlank()) {
            apiConsoleError = "API 路径不能为空。"
            return
        }
        scope.launch {
            apiConsoleLoading = true
            apiConsoleError = null
            apiConsoleResponse = ""
            try {
                if (body.isNotBlank() && method.uppercase() != "DELETE") {
                    Json.parseToJsonElement(body)
                }
                val result = when (method.uppercase()) {
                    "GET" -> currentApi.get(path)
                    "POST" -> currentApi.post(path, body.ifBlank { "{}" })
                    "PUT" -> currentApi.put(path, body.ifBlank { "{}" })
                    "PATCH" -> currentApi.patch(path, body.ifBlank { "{}" })
                    "DELETE" -> currentApi.delete(path)
                    else -> error("不支持的 HTTP 方法")
                }
                apiConsoleResponse = pretty(result)
            } catch (error: Exception) {
                apiConsoleError = error.message ?: "请求失败"
            } finally {
                apiConsoleLoading = false
            }
        }
    }

    fun openJsonAction(action: JsonActionState) {
        jsonActionError = null
        jsonAction = action
    }

    fun closeJsonAction() {
        jsonAction = null
        jsonActionError = null
    }

    fun submitJsonAction(identifier: String, text: String) {
        val action = jsonAction ?: return
        val currentApi = api ?: return
        scope.launch {
            try {
                jsonActionError = null
                val id = identifier.trim()
                if (action.requiresId && id.isBlank()) {
                    throw IllegalArgumentException("请填写唯一标识。")
                }
                if (text.isNotBlank() && action.method != "DELETE") {
                    Json.parseToJsonElement(text)
                }
                val path = action.pathTemplate.replace(
                    "{id}",
                    URLEncoder.encode(id, StandardCharsets.UTF_8)
                )
                var result = when (action.method.uppercase()) {
                    "POST" -> currentApi.post(path, if (text.isBlank()) "{}" else text)
                    "PUT" -> currentApi.put(path, if (text.isBlank()) "{}" else text)
                    "PATCH" -> currentApi.patch(path, if (text.isBlank()) "{}" else text)
                    "DELETE" -> currentApi.delete(path)
                    else -> throw IllegalArgumentException("不支持的请求方法：${action.method}")
                }
                if (action.followUpMethod != null && action.followUpPathTemplate != null) {
                    val followId = result.objectOrEmpty().string(action.followUpIdField)
                    if (followId.isBlank()) {
                        throw IllegalStateException("后续操作缺少响应字段 ${action.followUpIdField}")
                    }
                    val followPath = action.followUpPathTemplate.replace("{campaign_id}", url(followId))
                    result = when (action.followUpMethod.uppercase()) {
                        "POST" -> currentApi.post(followPath, "{}")
                        "PUT" -> currentApi.put(followPath, "{}")
                        "PATCH" -> currentApi.patch(followPath, "{}")
                        "DELETE" -> currentApi.delete(followPath)
                        else -> result
                    }
                }
                val reload = action.reloadSection
                jsonAction = null
                toast = action.successMessage
                if (reload != null) {
                    resources.remove(reload)
                    if (section == reload) {
                        loadSection(reload)
                    } else if (action.navigateOnSuccess) {
                        navigate(reload)
                    }
                }
            } catch (error: Exception) {
                jsonActionError = error.message ?: "操作失败"
            }
        }
    }

    fun openResourceAction(target: Section, item: ResourceItem?, operation: String) {
        val currentApi = api ?: return
        scope.launch {
            try {
                val action = when (target) {
                    Section.SKILLS -> buildSkillAction(currentApi, item, operation)
                    Section.MCP -> buildMcpAction(item, operation)
                    Section.AGENTS -> buildAgentAction(item, operation)
                    Section.MODELS -> buildModelAction(item, operation)
                    Section.PROJECTS -> buildProjectAction(item, operation)
                    Section.SCHEDULES -> buildScheduleAction(item, operation)
                    Section.BROWSER_ACCOUNTS -> buildBrowserAccountAction(item, operation)
                    Section.EXPERIENCE -> buildExperienceAction(item, operation)
                    Section.LOCAL_TOOLS -> buildLocalToolsAction(currentApi)
                    else -> null
                }
                if (action == null) {
                    toast = "该模块暂不支持此操作"
                } else {
                    openJsonAction(action)
                }
            } catch (error: Exception) {
                toast = error.message ?: "读取配置失败"
            }
        }
    }

    fun openProviderAction() {
        openJsonAction(
            JsonActionState(
                title = "添加模型供应商",
                description = "填写供应商协议、API 地址和密钥。密钥只在保存时提交。",
                initialJson = """{"name":"","provider":"openai_compatible","base_url":"","api_format":"auto","api_key_value":"","billing_scheme":"","billing_plan":""}""",
                method = "POST",
                pathTemplate = "/api/providers",
                reloadSection = Section.MODELS,
                successMessage = "供应商已保存"
            )
        )
    }

    fun openAssetCampaignAction() {
        openJsonAction(
            JsonActionState(
                title = "新建资产采集",
                description = "填写项目 ID、目标列表和排除资产；创建后会自动开始采集。",
                initialJson = """{"targets":[],"excluded_assets":[],"oneforall_path":"","brute":false}""",
                method = "POST",
                pathTemplate = "/api/projects/{id}/asset-campaigns",
                requiresId = true,
                idLabel = "项目 ID",
                reloadSection = Section.ASSETS,
                followUpMethod = "POST",
                followUpPathTemplate = "/api/asset-campaigns/{campaign_id}/start",
                followUpIdField = "campaign_id",
                successMessage = "资产采集已创建并启动"
            )
        )
    }

    fun toggleSchedule(id: String, enabled: Boolean) {
        val currentApi = api ?: return
        scope.launch {
            try {
                currentApi.patch("/api/schedules/${url(id)}", buildJsonObject { put("enabled", enabled) }.toString())
                toast = if (enabled) "自动任务已启用" else "自动任务已停用"
                resources.remove(Section.SCHEDULES)
                loadSection(Section.SCHEDULES)
            } catch (error: Exception) {
                toast = error.message
            }
        }
    }

    fun browserAccountAction(item: ResourceItem, action: String) {
        val currentApi = api ?: return
        val version = versionOf(item)
        scope.launch {
            try {
                currentApi.post(
                    "/api/browser-accounts/${url(item.id)}/${url(action)}",
                    buildJsonObject { put("expected_version", version) }.toString()
                )
                toast = when (action) {
                    "open" -> "已打开登录窗口"
                    "capture" -> "登录态已保存"
                    "clear" -> "账号缓存已清理"
                    else -> "操作已完成"
                }
                resources.remove(Section.BROWSER_ACCOUNTS)
                loadSection(Section.BROWSER_ACCOUNTS)
            } catch (error: Exception) {
                toast = error.message
            }
        }
    }

    fun localToolsAction(action: String) {
        val currentApi = api ?: return
        scope.launch {
            try {
                currentApi.post("/api/local-tools/${url(action)}", "{}")
                toast = when (action) {
                    "refresh" -> "本地工具索引已刷新"
                    "cleanup" -> "本地工具暂存已清理"
                    else -> "操作已完成"
                }
                resources.remove(Section.LOCAL_TOOLS)
                loadSection(Section.LOCAL_TOOLS)
            } catch (error: Exception) {
                toast = error.message
            }
        }
    }

    fun testModel(item: ResourceItem) {
        val currentApi = api ?: return
        scope.launch {
            try {
                val result = currentApi.post(
                    "/api/models/test",
                    buildJsonObject { put("model", item.id) }.toString()
                )
                toast = "模型测试完成：${result.toString().take(180)}"
            } catch (error: Exception) {
                toast = "模型测试失败：${error.message}"
            }
        }
    }

    fun importSkillArchive(path: Path) {
        val currentApi = api ?: return
        scope.launch {
            try {
                Files.newInputStream(path).use { stream ->
                    currentApi.postRawAsync(
                        "/api/resources/skills/import",
                        stream,
                        "application/octet-stream"
                    )
                }
                toast = "Skill 压缩包已导入"
                resources.remove(Section.SKILLS)
                loadSection(Section.SKILLS)
            } catch (error: Exception) {
                toast = "Skill 导入失败：${error.message}"
            }
        }
    }

    fun openProxyAction(operation: String, item: ResourceItem? = null) {
        val currentApi = api ?: return
        scope.launch {
            try {
                val root = currentApi.get("/api/proxies").objectOrEmpty()
                val version = root.long("version")
                val id = item?.id.orEmpty()
                val action = when (operation) {
                    "subscribe" -> JsonActionState(
                        title = "添加代理订阅",
                        description = "填写 HTTPS 订阅地址，后端会将其加密保存。",
                        initialJson = prettyJson.encodeToString(
                            JsonElement.serializer(),
                            buildJsonObject {
                                put("name", "")
                                put("url", "")
                                put("version", version)
                            }
                        ),
                        method = "POST",
                        pathTemplate = "/api/proxies/subscribe",
                        reloadSection = Section.PROXIES,
                        successMessage = "代理订阅已添加"
                    )
                    "import" -> JsonActionState(
                        title = "导入代理配置",
                        description = "粘贴 Clash/Mihomo 内联节点内容。",
                        initialJson = prettyJson.encodeToString(
                            JsonElement.serializer(),
                            buildJsonObject {
                                put("content", "")
                                put("version", version)
                            }
                        ),
                        method = "POST",
                        pathTemplate = "/api/proxies/import",
                        reloadSection = Section.PROXIES,
                        successMessage = "代理节点已导入"
                    )
                    "delete" -> JsonActionState(
                        title = "删除代理节点",
                        description = "确认删除选中的代理节点。",
                        initialJson = prettyJson.encodeToString(
                            JsonElement.serializer(),
                            buildJsonObject {
                                put("kind", "nodes")
                                put("id", id)
                                put("version", version)
                            }
                        ),
                        method = "POST",
                        pathTemplate = "/api/proxies/delete",
                        reloadSection = Section.PROXIES,
                        successMessage = "代理节点已删除"
                    )
                    "plan" -> JsonActionState(
                        title = "创建固定出口方案",
                        description = "按数组顺序填写节点 ID，形成固定链路。",
                        initialJson = prettyJson.encodeToString(
                            JsonElement.serializer(),
                            buildJsonObject {
                                put("name", "")
                                put("nodes", JsonArray(if (id.isBlank()) emptyList() else listOf(kotlinx.serialization.json.JsonPrimitive(id))))
                                put("version", version)
                            }
                        ),
                        method = "POST",
                        pathTemplate = "/api/proxies/plan",
                        reloadSection = Section.PROXIES,
                        successMessage = "固定出口方案已创建"
                    )
                    else -> JsonActionState(
                        title = "代理节点测速",
                        description = "选择 latency 或 download，nodes 为节点 ID 数组。",
                        initialJson = prettyJson.encodeToString(
                            JsonElement.serializer(),
                            buildJsonObject {
                                put("nodes", JsonArray(if (id.isBlank()) emptyList() else listOf(kotlinx.serialization.json.JsonPrimitive(id))))
                                put("mode", "latency")
                                put("version", version)
                            }
                        ),
                        method = "POST",
                        pathTemplate = "/api/proxies/benchmark",
                        reloadSection = Section.PROXIES,
                        successMessage = "测速任务已提交"
                    )
                }
                openJsonAction(action)
            } catch (error: Exception) {
                toast = error.message
            }
        }
    }

    fun toggleTheme() {
        darkTheme = !darkTheme
    }

    fun chooseDashboardPeriod(period: String) {
        dashboardPeriod = period
        scope.launch { loadDashboardInternal() }
    }

    fun login(username: String, password: String) {
        val currentApi = api ?: return
        scope.launch {
            try {
                loginError = null
                if (!currentApi.login(username, password)) {
                    loginError = "用户名或密码不正确。"
                    return@launch
                }
                requiresLogin = false
                booting = true
                bootMessage = "正在加载工作区…"
                loadDashboardInternal()
                loadSessionsInternal()
                booting = false
            } catch (error: Exception) {
                loginError = error.message ?: "登录失败"
            }
        }
    }

    fun openDataDirectory() {
        val path = Path.of(System.getenv("LOCALAPPDATA") ?: System.getProperty("user.home"))
            .resolve("SpiritScout")
        Files.createDirectories(path)
        java.awt.Desktop.getDesktop().open(path.toFile())
    }

    fun openLogFile() {
        backend.logPath?.let {
            if (Files.exists(it)) java.awt.Desktop.getDesktop().open(it.toFile())
        }
    }

    fun checkUpdates() {
        val currentApi = api ?: return
        scope.launch {
            try {
                settingsJson = currentApi.post("/api/updates/check", "{}")
                toast = "更新检查完成"
            } catch (error: Exception) {
                toast = "更新检查失败：${error.message}"
            }
        }
    }

    fun loadStorageOverview() {
        val currentApi = api ?: return
        scope.launch {
            try {
                settingsJson = currentApi.get("/api/storage/sessions")
            } catch (error: Exception) {
                toast = "存储读取失败：${error.message}"
            }
        }
    }

    fun logout() {
        val currentApi = api ?: return
        scope.launch {
            runCatching { currentApi.post("/api/auth/logout", "{}") }
            requiresLogin = true
            booting = false
            loginError = null
        }
    }

    suspend fun shutdown() {
        backend.stop()
        scope.cancel()
    }

    private suspend fun loadSection(target: Section) {
        when (target) {
            Section.AGENTS -> loadResourceInternal(target, "/api/agents", "roster") { item ->
                ResourceItem(
                    id = item.string("key"),
                    title = item.string("name"),
                    subtitle = item.string("duty"),
                    meta = "${item.string("modes").ifBlank { item.array("modes").joinToString(" · ") { it.toString().trim('"') }}} · ${item.string("status")}",
                    status = item.string("status"),
                    rawJson = pretty(item)
                )
            }
            Section.MODELS -> loadResourceInternal(target, "/api/models", "models") { item ->
                ResourceItem(
                    id = item.string("model"),
                    title = item.string("model"),
                    subtitle = item.string("provider").ifBlank { item.string("provider_ref") },
                    meta = item.array("capabilities").joinToString(" · ") { it.toString().trim('"') },
                    status = if (item.boolean("enabled")) "enabled" else "",
                    rawJson = pretty(item)
                )
            }
            Section.MCP -> loadResourceInternal(target, "/api/resources/mcp", "items") { item ->
                val data = item.element("data")?.objectOrEmpty() ?: item
                ResourceItem(
                    id = data.string("name").ifBlank { item.string("id") },
                    title = data.string("name").ifBlank { item.string("id") },
                    subtitle = data.string("command").ifBlank { data.string("url") },
                    meta = data.string("transport"),
                    status = if (data.boolean("enabled")) "enabled" else "disabled",
                    rawJson = pretty(item)
                )
            }
            Section.SKILLS -> loadPagedResourceInternal(target, "/api/resources/skills", "items") { item ->
                ResourceItem(
                    id = item.string("skill_id"),
                    title = item.string("skill_id"),
                    subtitle = item.string("description"),
                    meta = item.string("domain"),
                    status = item.string("severity"),
                    rawJson = pretty(item)
                )
            }
            Section.LOCAL_TOOLS -> loadResourceInternal(target, "/api/local-tools", "tools") { item ->
                ResourceItem(
                    id = item.string("id").ifBlank { item.string("path") },
                    title = item.string("name").ifBlank { item.string("path") },
                    subtitle = item.string("path"),
                    meta = item.string("kind"),
                    status = item.string("status"),
                    rawJson = pretty(item)
                )
            }
            Section.PROJECTS -> loadResourceInternal(target, "/api/projects", "projects") { item ->
                ResourceItem(
                    id = item.string("project_id"),
                    title = item.string("name"),
                    subtitle = item.string("description"),
                    meta = "资产 ${item.long("asset_count")} · 会话 ${item.long("session_count")}",
                    status = if (item.boolean("read_only")) "readonly" else "",
                    rawJson = pretty(item)
                )
            }
            Section.ASSETS -> loadPagedResourceInternal(target, "/api/assets", "items") { item ->
                ResourceItem(
                    id = item.string("key").ifBlank { item.string("value") },
                    title = item.string("value"),
                    subtitle = item.string("source"),
                    meta = item.string("asset_type").ifBlank { item.string("type") },
                    status = item.string("status"),
                    rawJson = pretty(item)
                )
            }
            Section.INTELLIGENCE -> loadResourceInternal(target, "/api/resources/vulnerability-taxonomy", "items") { item ->
                ResourceItem(
                    id = item.string("taxonomy_id").ifBlank { item.string("id") },
                    title = item.string("label").ifBlank { item.string("label_en") },
                    subtitle = item.string("label_en"),
                    meta = item.string("family").ifBlank { item.string("category") },
                    status = item.array("aliases").joinToString(" · ") { it.toString().trim('"') },
                    rawJson = pretty(item)
                )
            }
            Section.PROXIES -> loadResourceInternal(target, "/api/proxies", "nodes") { item ->
                ResourceItem(
                    id = item.string("id").ifBlank { item.string("name") },
                    title = item.string("name"),
                    subtitle = item.string("server"),
                    meta = item.string("type"),
                    status = item.string("verification"),
                    rawJson = pretty(item)
                )
            }
            Section.TRAFFIC -> loadResourceInternal(target, "/api/traffic", "sessions") { item ->
                ResourceItem(
                    id = item.string("session_id"),
                    title = item.string("title").ifBlank { item.string("target") },
                    subtitle = item.string("target"),
                    meta = "请求 ${item.long("total")} · 错误 ${item.long("errors")}",
                    status = item.string("status"),
                    rawJson = pretty(item)
                )
            }
            Section.SCHEDULES -> loadResourceInternal(target, "/api/schedules", "items") { item ->
                ResourceItem(
                    id = item.string("schedule_id").ifBlank { item.string("id") },
                    title = item.string("name"),
                    subtitle = item.string("message"),
                    meta = item.string("run_at"),
                    status = item.string("mode"),
                    rawJson = pretty(item)
                )
            }
            Section.EXPERIENCE -> loadPagedResourceInternal(target, "/api/experience", "items") { item ->
                ResourceItem(
                    id = item.string("insight_id").ifBlank { item.string("id") },
                    title = item.string("title").ifBlank { item.string("summary") },
                    subtitle = item.string("summary").ifBlank { item.string("body") },
                    meta = item.string("domain"),
                    status = item.string("status"),
                    rawJson = pretty(item)
                )
            }
            Section.BROWSER_ACCOUNTS -> loadResourceInternal(target, "/api/browser-accounts", "items") { item ->
                ResourceItem(
                    id = item.string("id").ifBlank { item.string("key") },
                    title = item.string("name").ifBlank { item.string("label") },
                    subtitle = item.string("username").ifBlank { item.string("profile") },
                    meta = item.string("browser"),
                    status = item.string("status"),
                    rawJson = pretty(item)
                )
            }
            Section.SETTINGS -> loadSystemInternal()
            Section.DASHBOARD -> loadDashboardInternal()
            Section.TASKS -> loadSessionsInternal()
            Section.WORKSPACE -> refreshWorkspaceInternal()
        }
    }

    private suspend fun loadDashboardInternal() {
        val currentApi = api ?: return
        loading[Section.DASHBOARD] = true
        errors[Section.DASHBOARD] = null
        try {
            val dashboard = currentApi.get("/api/dashboard?period=${url(dashboardPeriod)}").objectOrEmpty()
            val sessionList = currentApi.get("/api/sessions").objectOrEmpty()
            val systemRoot = currentApi.get("/api/system/resources").objectOrEmpty()
            val summary = dashboard.element("summary")?.objectOrEmpty() ?: dashboard
            val cache = summary.element("cache")?.objectOrEmpty()
            val hitRate = cache?.double("hit_rate") ?: 0.0
            val systemRootData = systemRoot.element("system")?.objectOrEmpty()
            val app = systemRoot.element("application")?.objectOrEmpty()
            system = SystemResourceSnapshot(
                cpu = systemRootData?.double("cpu_percent") ?: 0.0,
                memory = systemRootData?.double("memory_percent") ?: 0.0,
                appCpu = app?.double("cpu_percent") ?: 0.0,
                appMemoryMb = (app?.double("memory_rss") ?: 0.0) / 1048576.0,
                processCount = app?.int("process_count") ?: 0
            )

            metrics.clear()
            metrics += MetricItem(
                "会话总数",
                dashboard.long("total_sessions").toString(),
                "${dashboard.long("running")} 个运行中"
            )
            metrics += MetricItem(
                "模型调用",
                summary.long("calls").toString(),
                "今日请求"
            )
            metrics += MetricItem(
                "Token",
                formatTokens(summary.long("tokens")),
                "输入 ${formatTokens(summary.long("input_tokens"))}"
            )
            metrics += MetricItem(
                "缓存命中",
                if (hitRate > 0) "${(hitRate * 100).toInt()}%" else "—",
                if (hitRate > 0) "已有实测采样" else "等待请求",
                if (hitRate > 0) MetricTone.SUCCESS else MetricTone.NORMAL
            )

            sessions.clear()
            sessionList.array("sessions").forEach { sessions += SessionItem.fromJson(it) }
        } catch (error: Exception) {
            errors[Section.DASHBOARD] = error.message
        } finally {
            loading[Section.DASHBOARD] = false
        }
    }

    private suspend fun loadSessionsInternal() {
        val currentApi = api ?: return
        loading[Section.TASKS] = true
        errors[Section.TASKS] = null
        try {
            val root = currentApi.get("/api/sessions").objectOrEmpty()
            sessions.clear()
            root.array("sessions").forEach { sessions += SessionItem.fromJson(it) }
        } catch (error: Exception) {
            errors[Section.TASKS] = error.message
        } finally {
            loading[Section.TASKS] = false
        }
    }

    private suspend fun loadModelsInternal() {
        val currentApi = api ?: return
        try {
            val root = currentApi.get("/api/models").objectOrEmpty()
            models.clear()
            models += "自动"
            root.array("models").forEach {
                val name = it.objectOrEmpty().string("model")
                if (name.isNotBlank()) models += name
            }
        } catch (_: Exception) {
            if (models.isEmpty()) models += "自动"
        }
    }

    private suspend fun loadResourceInternal(
        target: Section,
        endpoint: String,
        arrayName: String,
        mapper: (kotlinx.serialization.json.JsonElement) -> ResourceItem
    ) {
        val currentApi = api ?: return
        loading[target] = true
        errors[target] = null
        try {
            val response = currentApi.get(endpoint)
            val root = response.objectOrEmpty()
            val items = when (response) {
                is JsonArray -> response
                else -> when {
                    root.element(arrayName) != null -> root.array(arrayName)
                    else -> root.array("items")
                }
            }
            resources[target] = items.map(mapper)
        } catch (error: Exception) {
            errors[target] = error.message
            resources.putIfAbsent(target, emptyList())
        } finally {
            loading[target] = false
        }
    }

    private suspend fun loadPagedResourceInternal(
        target: Section,
        endpoint: String,
        arrayName: String,
        mapper: (JsonElement) -> ResourceItem
    ) {
        val currentApi = api ?: return
        loading[target] = true
        errors[target] = null
        try {
            val collected = mutableListOf<ResourceItem>()
            var offset = 0
            var hasMore: Boolean
            do {
                val separator = if ('?' in endpoint) '&' else '?'
                val response = currentApi.get("$endpoint${separator}limit=${ResourcePaging.PAGE_SIZE}&offset=$offset")
                val root = response.objectOrEmpty()
                val items = when {
                    root.element(arrayName) != null -> root.array(arrayName)
                    else -> root.array("items")
                }
                collected += items.map(mapper)
                hasMore = root.element("has_more")?.toString()?.toBooleanStrictOrNull()
                    ?: (items.size >= ResourcePaging.PAGE_SIZE)
                offset = ResourcePaging.nextOffset(offset, ResourcePaging.PAGE_SIZE, items.size, hasMore)
                    ?: break
            } while (offset < 10_000)
            resources[target] = collected
        } catch (error: Exception) {
            errors[target] = error.message
            resources.putIfAbsent(target, emptyList())
        } finally {
            loading[target] = false
        }
    }

    private suspend fun refreshWorkspaceInternal() {
        val currentApi = api ?: return
        val current = selectedSession ?: return
        workspaceLoading = true
        workspaceError = null
        try {
            val root = currentApi.get("/api/sessions/${current.id}/state").objectOrEmpty()
            val session = root.element("session")?.let { SessionItem.fromJson(it) } ?: current
            selectedSession = session

            val timeline = mutableListOf<TimelineItem>()
            root.array("user_opinions").forEach {
                val text = it.objectOrEmpty().string("text")
                if (text.isNotBlank()) timeline += TimelineItem("你", text, TimelineKind.USER)
            }
            root.array("master_replies").forEach {
                val obj = it.objectOrEmpty()
                val text = obj.string("text").ifBlank { obj.string("detail") }.ifBlank { obj.string("content") }
                if (text.isNotBlank()) timeline += TimelineItem("灵枢", text, TimelineKind.MASTER)
            }
            root.array("findings").forEach {
                val obj = it.objectOrEmpty()
                val text = obj.string("text").ifBlank { obj.string("title") }
                if (text.isNotBlank()) timeline += TimelineItem("发现", text, TimelineKind.FINDING)
            }
            if (timeline.isEmpty()) {
                timeline += TimelineItem("灵枢", "任务已建立，等待下一轮执行状态。", TimelineKind.SYSTEM)
            }

            val tasks = root.array("tasks").map { raw ->
                val obj = raw.objectOrEmpty()
                val routes = obj.array("routes")
                WorkTask(
                    id = obj.string("task_id"),
                    title = obj.string("title").ifBlank { obj.string("task_type") },
                    agent = obj.string("agent_type"),
                    status = obj.string("status"),
                    summary = obj.string("result_summary"),
                    routesDone = routes.count {
                        it.objectOrEmpty().string("status") == "done"
                    },
                    routesTotal = routes.size
                )
            }

            val vulns = root.element("vulns")?.objectOrEmpty()?.long("total") ?: 0
            val flags = root.element("flags")?.objectOrEmpty()?.long("total") ?: 0
            val layers = root.element("layers")?.objectOrEmpty()?.mapValues { (_, value) ->
                value.toString().toLongOrNull() ?: 0
            } ?: emptyMap()
            val approvals = root.array("pending_approvals").map { raw ->
                val obj = raw.objectOrEmpty()
                PendingApprovalItem(
                    id = obj.string("approval_id").ifBlank { obj.string("id") },
                    action = obj.string("action"),
                    purpose = obj.string("purpose").ifBlank { obj.string("detail") },
                    detail = obj.string("detail")
                )
            }
            val questions = root.array("pending_questions").map { raw ->
                val obj = raw.objectOrEmpty()
                PendingQuestionItem(
                    id = obj.string("question_id").ifBlank { obj.string("id") },
                    agent = obj.string("agent"),
                    question = obj.string("question"),
                    options = obj.array("options").map { it.toString().trim('"') },
                    multiple = obj.boolean("multiple"),
                    secret = obj.boolean("secret"),
                    rawJson = pretty(raw)
                )
            }

            workspace = WorkspaceSnapshot(
                session = session,
                timeline = timeline.takeLast(60),
                tasks = tasks,
                findings = root.array("findings").size,
                vulnerabilities = vulns.toInt(),
                flags = flags.toInt(),
                pendingApprovals = approvals,
                pendingQuestions = questions,
                layers = layers,
                rawJson = pretty(root)
            )
            if (workspaceTab != WorkspaceTab.CHAT) {
                loadWorkspaceTabInternal(workspaceTab)
            }
        } catch (error: Exception) {
            workspaceError = error.message
        } finally {
            workspaceLoading = false
        }
    }

    private suspend fun loadWorkspaceTabInternal(tab: WorkspaceTab) {
        val currentApi = api ?: return
        val current = selectedSession ?: return
        workspaceTabLoading = true
        workspaceTabError = null
        try {
            val sid = current.id
            workspaceTabJson = when (tab) {
                WorkspaceTab.CHAT -> null
                WorkspaceTab.FILES,
                WorkspaceTab.DELIVERABLES -> currentApi.get("/api/sessions/$sid/deliverables?limit=100")
                WorkspaceTab.TOPOLOGY,
                WorkspaceTab.ASSETS,
                WorkspaceTab.OPPORTUNITIES,
                WorkspaceTab.CHALLENGES,
                WorkspaceTab.FLAGS -> currentApi.get("/api/sessions/$sid/state")
                WorkspaceTab.VULNERABILITIES -> currentApi.get("/api/sessions/$sid/vulnerabilities?limit=100")
                WorkspaceTab.EXPERIENCE -> currentApi.get("/api/sessions/$sid/experience")
                WorkspaceTab.TRAFFIC -> currentApi.get("/api/sessions/$sid/traffic")
                WorkspaceTab.BACKGROUND -> currentApi.get("/api/sessions/$sid/background-tasks")
                WorkspaceTab.EVENTS -> currentApi.get("/api/sessions/$sid/audit-page?activity=true&limit=250")
                WorkspaceTab.REPORT -> currentApi.get("/api/sessions/$sid/report")
            }
        } catch (error: Exception) {
            workspaceTabError = error.message
        } finally {
            workspaceTabLoading = false
        }
    }

    private suspend fun loadSystemInternal() {
        val currentApi = api ?: return
        loading[Section.SETTINGS] = true
        errors[Section.SETTINGS] = null
        try {
            val root = currentApi.get("/api/system/resources").objectOrEmpty()
            val systemData = root.element("system")?.objectOrEmpty()
            val app = root.element("application")?.objectOrEmpty()
            system = SystemResourceSnapshot(
                cpu = systemData?.double("cpu_percent") ?: 0.0,
                memory = systemData?.double("memory_percent") ?: 0.0,
                appCpu = app?.double("cpu_percent") ?: 0.0,
                appMemoryMb = (app?.double("memory_rss") ?: 0.0) / 1048576.0,
                processCount = app?.int("process_count") ?: 0
            )
        } catch (error: Exception) {
            errors[Section.SETTINGS] = error.message
        } finally {
            loading[Section.SETTINGS] = false
        }
    }

    private fun pretty(element: JsonElement): String =
        prettyJson.encodeToString(JsonElement.serializer(), element)

    private fun versionOf(item: ResourceItem?): Long =
        item?.let { runCatching { Json.parseToJsonElement(it.rawJson).jsonObject["version"]?.toString()?.toLong() }.getOrNull() }
            ?: 0L

    private suspend fun buildSkillAction(
        currentApi: SpiritApi,
        item: ResourceItem?,
        operation: String
    ): JsonActionState? {
        if (operation == "new") {
            return JsonActionState(
                title = "新建 Skill",
                description = "填写唯一标识和 Skill JSON；正文支持 Markdown。",
                initialJson = prettyJson.encodeToString(
                    JsonElement.serializer(),
                    Json.parseToJsonElement(
                        """{"data":{"skill_id":"my-skill","domain":"custom","description":"","body":"# 新 Skill\n\n适用条件：","enabled":true,"tier":"domain","aliases":[],"tags":[]},"expected_version":0}"""
                    )
                ),
                method = "PUT",
                pathTemplate = "/api/resources/skills/{id}",
                requiresId = true,
                reloadSection = Section.SKILLS,
                successMessage = "Skill 已保存"
            )
        }
        val current = item ?: return null
        val detail = currentApi.get("/api/resources/skills/${url(current.id)}")
        if (operation == "edit") {
            val body = buildJsonObject {
                put("data", detail)
                put("expected_version", detail.objectOrEmpty().long("version"))
            }
            return JsonActionState(
                title = "编辑 Skill · ${current.title}",
                description = "保存会创建新版本；正文和文件内容都会保留。",
                initialId = current.id,
                initialJson = pretty(body),
                method = "PUT",
                pathTemplate = "/api/resources/skills/{id}",
                reloadSection = Section.SKILLS,
                successMessage = "Skill 已更新"
            )
        }
        return JsonActionState(
            title = "删除 Skill · ${current.title}",
            description = "确认删除后，该 Skill 将从目录中移除。",
            initialId = current.id,
            method = "DELETE",
            pathTemplate = "/api/resources/skills/{id}?expected_version=${detail.objectOrEmpty().long("version")}",
            reloadSection = Section.SKILLS,
            successMessage = "Skill 已删除"
        )
    }

    private fun buildMcpAction(item: ResourceItem?, operation: String): JsonActionState? {
        if (operation == "new") {
            return JsonActionState(
                title = "新建 MCP",
                description = "填写唯一名称以及 stdio/http/sse 连接配置。",
                initialJson = prettyJson.encodeToString(
                    JsonElement.serializer(),
                    Json.parseToJsonElement(
                        """{"data":{"name":"my-mcp","transport":"stdio","command":"","args":[],"url":"","timeout":180.0,"default_risk":"readonly","risk_by_action":true,"tool_risk_overrides":{},"internet_search":false,"scope_enforcement":"required","target_params":[],"allowed_agents":[],"enabled":false,"headers":{},"env":{}},"expected_version":0}"""
                    )
                ),
                method = "PUT",
                pathTemplate = "/api/resources/mcp/{id}",
                requiresId = true,
                reloadSection = Section.MCP,
                successMessage = "MCP 已保存"
            )
        }
        val current = item ?: return null
        val root = runCatching { Json.parseToJsonElement(current.rawJson).jsonObject }.getOrNull() ?: return null
        val data = root["data"] ?: return null
        val version = root["version"]?.toString()?.toLongOrNull() ?: 0L
        if (operation == "edit") {
            val body = buildJsonObject {
                put("data", data)
                put("expected_version", version)
            }
            return JsonActionState(
                title = "编辑 MCP · ${current.title}",
                description = "修改连接方式、命令、参数、权限和启用状态。",
                initialId = current.id,
                initialJson = pretty(body),
                method = "PUT",
                pathTemplate = "/api/resources/mcp/{id}",
                reloadSection = Section.MCP,
                successMessage = "MCP 已更新"
            )
        }
        return JsonActionState(
            title = "删除 MCP · ${current.title}",
            description = "确认删除该 MCP 接入配置。",
            initialId = current.id,
            method = "DELETE",
            pathTemplate = "/api/resources/mcp/{id}?expected_version=$version",
            reloadSection = Section.MCP,
            successMessage = "MCP 已删除"
        )
    }

    private fun buildAgentAction(item: ResourceItem?, operation: String): JsonActionState? {
        val current = item ?: return null
        if (operation != "edit") return null
        return JsonActionState(
            title = "绑定模型 · ${current.title}",
            description = "填写模型 ID；留空表示使用系统默认模型。",
            initialId = current.id,
            initialJson = """{"model":""}""",
            method = "PUT",
            pathTemplate = "/api/agents/{id}",
            reloadSection = Section.AGENTS,
            successMessage = "编队模型已更新"
        )
    }

    private fun buildModelAction(item: ResourceItem?, operation: String): JsonActionState? {
        if (operation == "new") {
            return JsonActionState(
                title = "添加模型",
                description = "填写模型 ID、供应商引用和能力。",
                initialJson = """{"model":"","provider_ref":"","context_window":0,"max_output_tokens":0,"capabilities":[],"reasoning_effort_levels":[],"prompt_caching":"provider_default"}""",
                method = "POST",
                pathTemplate = "/api/models",
                reloadSection = Section.MODELS,
                successMessage = "模型已保存"
            )
        }
        val current = item ?: return null
        return if (operation == "edit") {
            JsonActionState(
                title = "编辑模型 · ${current.title}",
                description = "修改能力、上下文窗口和缓存策略。",
                initialId = current.id,
                initialJson = current.rawJson,
                method = "PUT",
                pathTemplate = "/api/models/{id}",
                reloadSection = Section.MODELS,
                successMessage = "模型已更新"
            )
        } else {
            JsonActionState(
                title = "删除模型 · ${current.title}",
                description = "确认从模型注册表删除该模型。",
                initialId = current.id,
                method = "DELETE",
                pathTemplate = "/api/models/{id}",
                reloadSection = Section.MODELS,
                successMessage = "模型已删除"
            )
        }
    }

    private fun buildProjectAction(item: ResourceItem?, operation: String): JsonActionState? {
        if (operation == "new") {
            return JsonActionState(
                title = "新建项目",
                description = "项目用于关联资产、会话、漏洞和报告。",
                initialJson = """{"name":"新项目","description":""}""",
                method = "POST",
                pathTemplate = "/api/projects",
                reloadSection = Section.PROJECTS,
                successMessage = "项目已创建"
            )
        }
        val current = item ?: return null
        return if (operation == "edit") {
            JsonActionState(
                title = "编辑项目 · ${current.title}",
                description = "修改项目名称和说明。",
                initialId = current.id,
                initialJson = current.rawJson,
                method = "PUT",
                pathTemplate = "/api/projects/{id}",
                reloadSection = Section.PROJECTS,
                successMessage = "项目已更新"
            )
        } else {
            JsonActionState(
                title = "删除项目 · ${current.title}",
                description = "项目内的资产和记录会按后端规则处理，请确认后删除。",
                initialId = current.id,
                method = "DELETE",
                pathTemplate = "/api/projects/{id}?confirm=true",
                reloadSection = Section.PROJECTS,
                successMessage = "项目已删除"
            )
        }
    }

    private fun buildScheduleAction(item: ResourceItem?, operation: String): JsonActionState? {
        if (operation == "new") {
            val runAt = Instant.now().plusSeconds(3600).toString()
            return JsonActionState(
                title = "新建自动任务",
                description = "填写任务内容、执行时间和周期。",
                initialJson = prettyJson.encodeToString(
                    JsonElement.serializer(),
                    Json.parseToJsonElement(
                        """{"name":"自动任务","message":"","mode":"src","target":"","scope":[],"model":"","project_id":"","run_at":"$runAt","interval_seconds":null}"""
                    )
                ),
                method = "POST",
                pathTemplate = "/api/schedules",
                reloadSection = Section.SCHEDULES,
                successMessage = "自动任务已创建"
            )
        }
        val current = item ?: return null
        return if (operation == "edit") {
            JsonActionState(
                title = "编辑自动任务 · ${current.title}",
                description = "修改后请保存；可直接在 JSON 中调整 run_at 和周期。",
                initialId = current.id,
                initialJson = current.rawJson,
                method = "PUT",
                pathTemplate = "/api/schedules/{id}",
                reloadSection = Section.SCHEDULES,
                successMessage = "自动任务已更新"
            )
        } else {
            JsonActionState(
                title = "删除自动任务 · ${current.title}",
                description = "确认删除该计划任务。",
                initialId = current.id,
                method = "DELETE",
                pathTemplate = "/api/schedules/{id}",
                reloadSection = Section.SCHEDULES,
                successMessage = "自动任务已删除"
            )
        }
    }

    private fun buildBrowserAccountAction(item: ResourceItem?, operation: String): JsonActionState? {
        if (operation == "new") {
            val id = "account-" + System.currentTimeMillis().toString(36)
            return JsonActionState(
                title = "新建浏览器账号",
                description = "配置来源域名、MFA 方式、保留策略和可选登录态。",
                initialId = id,
                initialJson = prettyJson.encodeToString(
                    JsonElement.serializer(),
                    Json.parseToJsonElement(
                        """{"data":{"name":"新账号","role":"","origins":[],"retain":true,"ttl_days":30,"mfa_method":"none","clear_credentials":false},"expected_version":0}"""
                    )
                ),
                method = "PUT",
                pathTemplate = "/api/browser-accounts/{id}",
                requiresId = true,
                reloadSection = Section.BROWSER_ACCOUNTS,
                successMessage = "浏览器账号已保存"
            )
        }
        val current = item ?: return null
        val root = runCatching { Json.parseToJsonElement(current.rawJson).jsonObject }.getOrNull() ?: return null
        val version = root["version"]?.toString()?.toLongOrNull() ?: 0L
        if (operation == "edit") {
            val data = buildJsonObject {
                root.forEach { (key, value) ->
                    if (key !in setOf("id", "version", "created_at", "updated_at", "status")) {
                        put(key, value)
                    }
                }
            }
            val body = buildJsonObject {
                put("data", data)
                put("expected_version", version)
            }
            return JsonActionState(
                title = "编辑浏览器账号 · ${current.title}",
                description = "留空的敏感字段会保持原值。",
                initialId = current.id,
                initialJson = pretty(body),
                method = "PUT",
                pathTemplate = "/api/browser-accounts/{id}",
                reloadSection = Section.BROWSER_ACCOUNTS,
                successMessage = "浏览器账号已更新"
            )
        }
        return JsonActionState(
            title = "删除浏览器账号 · ${current.title}",
            description = "将删除账号及已保存的登录态和凭据。",
            initialId = current.id,
            method = "DELETE",
            pathTemplate = "/api/browser-accounts/{id}?expected_version=$version",
            reloadSection = Section.BROWSER_ACCOUNTS,
            successMessage = "浏览器账号已删除"
        )
    }

    private fun buildExperienceAction(item: ResourceItem?, operation: String): JsonActionState? {
        val current = item ?: return null
        if (operation != "delete") return null
        return JsonActionState(
            title = "删除经验 · ${current.title}",
            description = "确认删除该条经验记录。",
            initialId = current.id,
            method = "DELETE",
            pathTemplate = "/api/experience/{id}",
            reloadSection = Section.EXPERIENCE,
            successMessage = "经验已删除"
        )
    }

    private suspend fun buildLocalToolsAction(currentApi: SpiritApi): JsonActionState {
        val root = currentApi.get("/api/local-tools").objectOrEmpty()
        val config = root.element("config") ?: JsonObject(emptyMap())
        return JsonActionState(
            title = "本地工具配置",
            description = "配置启用状态、目录、超时和扫描上限。",
            initialJson = pretty(config),
            method = "PUT",
            pathTemplate = "/api/local-tools",
            reloadSection = Section.LOCAL_TOOLS,
            successMessage = "本地工具配置已保存"
        )
    }

    private fun url(value: String): String =
        URLEncoder.encode(value, StandardCharsets.UTF_8)

    private companion object {
        val prettyJson = Json { prettyPrint = true }
    }

    private fun formatTokens(value: Long): String = when {
        value >= 1_000_000 -> "%.2fM".format(value / 1_000_000.0)
        value >= 1_000 -> "%.1fK".format(value / 1_000.0)
        else -> value.toString()
    }
}
