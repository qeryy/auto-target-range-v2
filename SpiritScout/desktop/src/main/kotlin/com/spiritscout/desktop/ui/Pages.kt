package com.spiritscout.desktop.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.ArrowBack
import androidx.compose.material.icons.outlined.AttachFile
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.ChevronRight
import androidx.compose.material.icons.outlined.Description
import androidx.compose.material.icons.outlined.ErrorOutline
import androidx.compose.material.icons.outlined.FolderOpen
import androidx.compose.material.icons.outlined.Memory
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.PauseCircle
import androidx.compose.material.icons.outlined.PlayArrow
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material.icons.outlined.Send
import androidx.compose.material.icons.outlined.Speed
import androidx.compose.material.icons.outlined.StopCircle
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.moriafly.salt.ui.Button
import com.moriafly.salt.ui.ButtonAppearance
import com.moriafly.salt.ui.ButtonIntent
import com.moriafly.salt.ui.Card
import com.moriafly.salt.ui.Icon
import com.moriafly.salt.ui.SaltTheme
import com.moriafly.salt.ui.Text
import com.moriafly.salt.ui.UnstableSaltUiApi
import com.spiritscout.desktop.AppController
import com.spiritscout.desktop.model.MetricTone
import com.spiritscout.desktop.model.ResourceItem
import com.spiritscout.desktop.model.Section
import com.spiritscout.desktop.model.SessionItem
import com.spiritscout.desktop.model.TimelineItem
import com.spiritscout.desktop.model.TimelineKind
import com.spiritscout.desktop.model.WorkTask
import com.spiritscout.desktop.model.WorkspaceTab
import com.spiritscout.desktop.model.array
import com.spiritscout.desktop.model.element
import com.spiritscout.desktop.model.objectOrEmpty
import com.spiritscout.desktop.model.string
import javax.swing.JFileChooser
import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject

@OptIn(UnstableSaltUiApi::class)
@Composable
fun PageContent(controller: AppController) {
    when (controller.section) {
        Section.DASHBOARD -> DashboardPage(controller)
        Section.TASKS -> TasksPage(controller)
        Section.WORKSPACE -> WorkspacePage(controller)
        Section.SETTINGS -> SettingsPage(controller)
        else -> ResourcePage(controller, controller.section)
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
fun LoginPage(controller: AppController) {
    var username by remember { mutableStateOf("spiritscout") }
    var password by remember { mutableStateOf("") }
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Card(Modifier.width(430.dp), color = SaltTheme.colors.subBackground) {
            Column(Modifier.padding(30.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                Box(
                    Modifier.size(46.dp).clip(RoundedCornerShape(12.dp))
                        .background(SaltTheme.colors.highlight),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(Icons.Outlined.Lock, null, tint = SaltTheme.colors.onHighlight, modifier = Modifier.size(22.dp))
                }
                Text("登录灵枢控制台", style = SaltTheme.textStyles.largeTitle)
                Text(
                    "本机后端已启动，但配置了管理员凭据。请输入账号密码。",
                    style = SaltTheme.textStyles.sub,
                    color = SaltTheme.colors.subText
                )
                Column(verticalArrangement = Arrangement.spacedBy(7.dp)) {
                    Text("管理员", style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText)
                    SpiritTextField(username, { username = it }, "管理员用户名")
                }
                Column(verticalArrangement = Arrangement.spacedBy(7.dp)) {
                    Text("密码", style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText)
                    SpiritPasswordField(password, { password = it }, "管理员密码")
                }
                controller.loginError?.let {
                    Text(it, color = SaltTheme.colors.error, style = SaltTheme.textStyles.sub)
                }
                Button(
                    onClick = { controller.login(username.trim(), password) },
                    text = "登录",
                    enabled = username.isNotBlank() && password.isNotBlank(),
                    modifier = Modifier.fillMaxWidth()
                )
            }
        }
    }
}

@Composable
private fun DashboardPage(controller: AppController) {
    LaunchedEffect(Unit) {
        while (isActive) {
            delay(15_000)
            controller.refreshCurrent()
        }
    }
    val error = controller.errors[Section.DASHBOARD]
    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(28.dp),
        verticalArrangement = Arrangement.spacedBy(18.dp)
    ) {
        if (error != null) {
            item { ErrorBlock(error) }
        }
        item {
            Row(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                listOf(
                    "hour" to "近 1 小时",
                    "today" to "今天",
                    "7d" to "近 7 天",
                    "30d" to "近 30 天",
                    "all" to "全部"
                ).forEach { (value, label) ->
                    val active = controller.dashboardPeriod == value
                    Box(
                        modifier = Modifier
                            .clip(RoundedCornerShape(50))
                            .background(
                                if (active) SaltTheme.colors.highlight
                                else SaltTheme.colors.subBackground
                            )
                            .clickable { controller.chooseDashboardPeriod(value) }
                            .padding(horizontal = 12.dp, vertical = 7.dp)
                    ) {
                        Text(
                            label,
                            style = SaltTheme.textStyles.sub,
                            color = if (active) SaltTheme.colors.onHighlight else SaltTheme.colors.subText
                        )
                    }
                }
            }
        }
        item {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                controller.metrics.forEach { metric ->
                    MetricCard(metric, Modifier.weight(1f))
                }
            }
        }
        item {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(18.dp)
            ) {
                RecentSessionsCard(
                    sessions = controller.sessions.take(5),
                    onOpen = controller::openSession,
                    modifier = Modifier.weight(1.55f)
                )
                SystemPulseCard(controller, Modifier.weight(0.85f))
            }
        }
        item {
            DeliveryCard(controller)
        }
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
private fun RecentSessionsCard(
    sessions: List<SessionItem>,
    onOpen: (SessionItem) -> Unit,
    modifier: Modifier = Modifier
) {
    Card(modifier = modifier.height(370.dp), color = SaltTheme.colors.subBackground) {
        Column(Modifier.fillMaxSize().padding(20.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    "最近任务",
                    style = SaltTheme.textStyles.main,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.weight(1f)
                )
                Text("${sessions.size} 个", style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText)
            }
            Spacer(Modifier.height(14.dp))
            if (sessions.isEmpty()) {
                EmptyState("还没有任务", "点击右上角「新任务」开始。")
            } else {
                LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    items(sessions, key = { it.id }) { session ->
                        CompactSessionRow(session, onClick = { onOpen(session) })
                    }
                }
            }
        }
    }
}

@Composable
private fun CompactSessionRow(session: SessionItem, onClick: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Box(
            modifier = Modifier.size(34.dp).clip(CircleShape)
                .background(SaltTheme.colors.highlight.copy(alpha = 0.12f)),
            contentAlignment = Alignment.Center
        ) {
            Text(session.mode.take(1).ifBlank { "任" }, color = SaltTheme.colors.highlight, fontWeight = FontWeight.Bold)
        }
        Column(Modifier.weight(1f)) {
            Text(session.title, maxLines = 1, overflow = TextOverflow.Ellipsis, fontWeight = FontWeight.Medium)
            Text(
                listOf(session.target, session.createdAt).filter { it.isNotBlank() }.joinToString(" · "),
                style = SaltTheme.textStyles.sub,
                color = SaltTheme.colors.subText,
                maxLines = 1
            )
        }
        StatusPill(session.stateLabel, sessionStatusColor(session))
        Icon(Icons.Outlined.ChevronRight, null, modifier = Modifier.size(16.dp), tint = SaltTheme.colors.subText)
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
private fun SystemPulseCard(controller: AppController, modifier: Modifier = Modifier) {
    val system = controller.system
    Card(modifier = modifier.height(370.dp), color = SaltTheme.colors.subBackground) {
        Column(Modifier.fillMaxSize().padding(20.dp)) {
            Text("本机状态", style = SaltTheme.textStyles.main, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(16.dp))
            GaugeRow(Icons.Outlined.Speed, "CPU", system?.cpu ?: 0.0)
            Spacer(Modifier.height(14.dp))
            GaugeRow(Icons.Outlined.Memory, "内存", system?.memory ?: 0.0)
            Spacer(Modifier.height(22.dp))
            Box(Modifier.fillMaxWidth().height(1.dp).background(SaltTheme.colors.stroke))
            Spacer(Modifier.height(18.dp))
            InfoLine("后端", controller.backendUrl.ifBlank { "未连接" })
            Spacer(Modifier.height(10.dp))
            InfoLine("运行时", controller.runtimeName.ifBlank { "准备中" })
            Spacer(Modifier.height(10.dp))
            InfoLine("应用内存", "%.1f MB".format(system?.appMemoryMb ?: 0.0))
            Spacer(Modifier.height(10.dp))
            InfoLine("子进程", "${system?.processCount ?: 0} 个")
        }
    }
}

@Composable
private fun GaugeRow(
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    label: String,
    percent: Double
) {
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        Icon(icon, null, modifier = Modifier.size(18.dp), tint = SaltTheme.colors.subText)
        Text(label, modifier = Modifier.width(42.dp), style = SaltTheme.textStyles.sub)
        Box(
            modifier = Modifier.weight(1f).height(7.dp).clip(RoundedCornerShape(50))
                .background(SaltTheme.colors.stroke)
        ) {
            Box(
                modifier = Modifier.fillMaxWidth((percent.coerceIn(0.0, 100.0) / 100.0).toFloat())
                    .fillMaxHeight()
                    .clip(RoundedCornerShape(50))
                    .background(
                        when {
                            percent >= 90 -> SaltTheme.colors.error
                            percent >= 70 -> SaltTheme.colors.warning
                            else -> SaltTheme.colors.highlight
                        }
                    )
            )
        }
        Text("%.0f%%".format(percent), style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText)
    }
}

@Composable
private fun InfoLine(label: String, value: String) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(label, style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText, modifier = Modifier.width(76.dp))
        Text(value, style = SaltTheme.textStyles.sub, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
private fun DeliveryCard(controller: AppController) {
    val completed = controller.sessions.count { it.status == "completed" }
    val running = controller.sessions.count { it.status == "running" }
    Card(modifier = Modifier.fillMaxWidth(), color = SaltTheme.colors.subBackground) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(20.dp),
            horizontalArrangement = Arrangement.spacedBy(22.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Icon(
                Icons.Outlined.CheckCircle,
                null,
                tint = SaltTheme.colors.success,
                modifier = Modifier.size(28.dp)
            )
            Column(Modifier.weight(1f)) {
                Text("执行概况", fontWeight = FontWeight.SemiBold)
                Text(
                    "已完成 $completed 个，运行中 $running 个。任务详情可在工作区继续对话和查看证据。",
                    style = SaltTheme.textStyles.sub,
                    color = SaltTheme.colors.subText
                )
            }
            Button(
                onClick = { controller.navigate(Section.TASKS) },
                text = "查看全部任务",
                appearance = ButtonAppearance.Subtle
            )
        }
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
private fun TasksPage(controller: AppController) {
    var query by remember { mutableStateOf("") }
    val filtered = controller.sessions.filter {
        query.isBlank() || listOf(it.title, it.target, it.mode, it.stateLabel)
            .any { field -> field.contains(query, ignoreCase = true) }
    }
    val error = controller.errors[Section.TASKS]
    Column(Modifier.fillMaxSize().padding(28.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(14.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(Modifier.weight(1f)) {
                SpiritTextField(query, { query = it }, "搜索任务、目标或模式")
                Icon(
                    Icons.Outlined.Search,
                    null,
                    modifier = Modifier.align(Alignment.CenterEnd).padding(end = 14.dp).size(17.dp),
                    tint = SaltTheme.colors.subText
                )
            }
            StatusPill("${filtered.size} 个任务", SaltTheme.colors.highlight)
        }
        Spacer(Modifier.height(16.dp))
        if (error != null) {
            ErrorBlock(error)
            Spacer(Modifier.height(14.dp))
        }
        Card(Modifier.fillMaxSize(), color = SaltTheme.colors.subBackground) {
            if (controller.loading[Section.TASKS] == true && filtered.isEmpty()) {
                LoadingBlock()
            } else if (filtered.isEmpty()) {
                EmptyState("没有匹配的任务", "修改搜索词，或创建新任务。")
            } else {
                LazyColumn(
                    contentPadding = androidx.compose.foundation.layout.PaddingValues(12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    items(filtered, key = { it.id }) { session ->
                        SessionCard(session, onClick = { controller.openSession(session) })
                    }
                }
            }
        }
    }
}

@Composable
private fun SessionCard(session: SessionItem, onClick: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        Box(
            modifier = Modifier.size(42.dp).clip(RoundedCornerShape(10.dp))
                .background(SaltTheme.colors.highlight.copy(alpha = 0.12f)),
            contentAlignment = Alignment.Center
        ) {
            Text(session.mode.ifBlank { "任务" }, color = SaltTheme.colors.highlight, fontSize = 11.sp, fontWeight = FontWeight.SemiBold)
        }
        Column(Modifier.weight(1.4f)) {
            Text(session.title, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Spacer(Modifier.height(3.dp))
            Text(
                listOf(session.target, session.model).filter { it.isNotBlank() }.joinToString(" · "),
                style = SaltTheme.textStyles.sub,
                color = SaltTheme.colors.subText,
                maxLines = 1
            )
        }
        Column(Modifier.width(110.dp)) {
            StatusPill(session.stateLabel, sessionStatusColor(session))
        }
        Column(Modifier.width(120.dp)) {
            Text("Token ${formatNumber(session.tokens)}", style = SaltTheme.textStyles.sub)
            Text("${session.rounds} 轮 · ${session.createdAt}", style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText)
        }
        Icon(Icons.Outlined.ChevronRight, null, modifier = Modifier.size(18.dp), tint = SaltTheme.colors.subText)
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
private fun WorkspacePage(controller: AppController) {
    LaunchedEffect(controller.selectedSession?.id) {
        while (isActive) {
            delay(3_000)
            controller.refreshCurrent()
        }
    }
    val workspace = controller.workspace
    val session = controller.selectedSession
    Column(Modifier.fillMaxSize().padding(24.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            SpiritIconButton(Icons.Outlined.ArrowBack, "返回任务", controller::returnToTasks)
            Column(Modifier.weight(1f)) {
                Text(session?.title ?: "任务工作区", style = SaltTheme.textStyles.largeTitle)
                Text(
                    listOfNotNull(session?.target, session?.model, workspace?.session?.stateLabel)
                        .filter { it.isNotBlank() }.joinToString(" · "),
                    style = SaltTheme.textStyles.sub,
                    color = SaltTheme.colors.subText
                )
            }
            if (workspace != null) {
                StatusPill(workspace.session.stateLabel, sessionStatusColor(workspace.session))
                Button(
                    onClick = controller::changeSessionModel,
                    text = "模型",
                    appearance = ButtonAppearance.Subtle
                )
                Button(
                    onClick = { controller.openSessionManageAction("rename") },
                    text = "重命名",
                    appearance = ButtonAppearance.Subtle
                )
                Button(
                    onClick = { controller.openSessionManageAction("project") },
                    text = "项目",
                    appearance = ButtonAppearance.Subtle
                )
                Button(
                    onClick = { controller.openSessionManageAction("delete") },
                    text = "删除",
                    appearance = ButtonAppearance.Subtle,
                    intent = ButtonIntent.Destructive
                )
                if (workspace.session.status == "running") {
                    Button(
                        onClick = { controller.sessionAction(if (workspace.session.paused) "resume" else "pause") },
                        text = if (workspace.session.paused) "继续" else "暂停",
                        appearance = ButtonAppearance.Subtle,
                        leadingIcon = {
                            Icon(
                                if (workspace.session.paused) Icons.Outlined.PlayArrow else Icons.Outlined.PauseCircle,
                                null,
                                modifier = Modifier.size(16.dp)
                            )
                        }
                    )
                    Button(
                        onClick = { controller.sessionAction("stop") },
                        text = "结束",
                        appearance = ButtonAppearance.Subtle,
                        intent = ButtonIntent.Destructive,
                        leadingIcon = {
                            Icon(Icons.Outlined.StopCircle, null, modifier = Modifier.size(16.dp))
                        }
                    )
                }
            }
        }
        Spacer(Modifier.height(12.dp))
        WorkspaceTabBar(
            selected = controller.workspaceTab,
            onSelect = controller::selectWorkspaceTab
        )
        Spacer(Modifier.height(12.dp))
        if (controller.workspaceError != null) {
            ErrorBlock(controller.workspaceError!!)
            Spacer(Modifier.height(12.dp))
        }
        Row(Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            if (controller.workspaceTab == WorkspaceTab.CHAT) {
                ConversationCard(controller, Modifier.weight(1f))
            } else {
                WorkspaceDataCard(controller, Modifier.weight(1f))
            }
            WorkspaceSidebar(controller, workspace?.tasks.orEmpty(), workspace?.let {
                WorkspaceStats(
                    it.layers,
                    it.findings,
                    it.vulnerabilities,
                    it.flags,
                    it.pendingApprovals.size,
                    it.pendingQuestions.size
                )
            }, Modifier.width(310.dp))
        }
    }
}

@Composable
private fun WorkspaceTabBar(selected: WorkspaceTab, onSelect: (WorkspaceTab) -> Unit) {
    LazyRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        items(WorkspaceTab.entries, key = { it.name }) { tab ->
            val active = tab == selected
            Box(
                modifier = Modifier
                    .clip(RoundedCornerShape(50))
                    .background(
                        if (active) SaltTheme.colors.highlight
                        else SaltTheme.colors.subBackground
                    )
                    .clickable { onSelect(tab) }
                    .padding(horizontal = 13.dp, vertical = 7.dp)
            ) {
                Text(
                    tab.title,
                    style = SaltTheme.textStyles.sub,
                    color = if (active) SaltTheme.colors.onHighlight else SaltTheme.colors.subText,
                    fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal
                )
            }
        }
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
private fun WorkspaceDataCard(controller: AppController, modifier: Modifier = Modifier) {
    val tab = controller.workspaceTab
    val data = controller.workspaceTabJson
    Card(modifier.fillMaxHeight(), color = SaltTheme.colors.subBackground) {
        Column(Modifier.fillMaxSize()) {
            Row(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 18.dp, vertical = 13.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(tab.title, modifier = Modifier.weight(1f), fontWeight = FontWeight.SemiBold)
                if (controller.workspaceTabLoading) {
                    Text("同步中…", style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText)
                } else {
                    Text(
                        "每 3 秒同步",
                        style = SaltTheme.textStyles.sub,
                        color = SaltTheme.colors.subText
                    )
                }
                val sid = controller.selectedSession?.id
                if (sid != null && tab == WorkspaceTab.REPORT) {
                    Button(
                        onClick = { controller.openBackendUrl("/api/sessions/${urlPart(sid)}/report/download") },
                        text = "下载报告",
                        appearance = ButtonAppearance.Subtle
                    )
                }
                if (sid != null && tab == WorkspaceTab.EVENTS) {
                    Button(
                        onClick = { controller.openBackendUrl("/api/sessions/${urlPart(sid)}/audit/download") },
                        text = "导出审计",
                        appearance = ButtonAppearance.Subtle
                    )
                }
            }
            Box(Modifier.fillMaxWidth().height(1.dp).background(SaltTheme.colors.stroke))
            when {
                controller.workspaceTabError != null -> ErrorBlock(controller.workspaceTabError!!)
                controller.workspaceTabLoading && data == null -> LoadingBlock("正在加载${tab.title}…")
                data == null -> EmptyState("暂无数据", "当前标签页还没有可展示内容。")
                tab == WorkspaceTab.REPORT -> WorkspaceReport(data)
                else -> {
                    val items = extractWorkspaceItems(tab, data)
                    if (items.isEmpty()) {
                        EmptyState("暂无${tab.title}记录", "该会话当前没有返回条目。")
                    } else {
                        LazyColumn(
                            modifier = Modifier.fillMaxSize(),
                            contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp),
                            verticalArrangement = Arrangement.spacedBy(9.dp)
                        ) {
                            itemsIndexed(items) { index, item ->
                                WorkspaceJsonRow(controller, tab, index, item)
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun WorkspaceReport(data: JsonElement) {
    val root = data.objectOrEmpty()
    val markdown = root.string("markdown").ifBlank { root.string("content") }.ifBlank { data.toString() }
    SelectionContainer {
        Text(
            markdown,
            modifier = Modifier.fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(20.dp),
            style = SaltTheme.textStyles.paragraph,
            color = SaltTheme.colors.text
        )
    }
}

@Composable
private fun WorkspaceJsonRow(
    controller: AppController,
    tab: WorkspaceTab,
    index: Int,
    item: JsonElement
) {
    val obj = item.objectOrEmpty()
    val title = listOf(
        "title",
        "name",
        "label",
        "asset",
        "url",
        "path",
        "task_id",
        "job_id",
        "vuln_id",
        "record_id",
        "type"
    ).firstNotNullOfOrNull { key -> obj.string(key).takeIf { it.isNotBlank() } }
        ?: "${tab.title} #${index + 1}"
    val detail = listOf(
        "description",
        "text",
        "summary",
        "result_summary",
        "command",
        "endpoint",
        "message",
        "note",
        "status"
    ).firstNotNullOfOrNull { key -> obj.string(key).takeIf { it.isNotBlank() } }
        ?: JsonObject(emptyMap()).toString()
    val badge = listOf("status", "severity", "type", "level", "kind")
        .firstNotNullOfOrNull { key -> obj.string(key).takeIf { it.isNotBlank() } }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(9.dp))
            .background(SaltTheme.colors.background.copy(alpha = 0.5f))
            .padding(horizontal = 14.dp, vertical = 11.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (detail.isNotBlank()) {
                Text(
                    detail,
                    style = SaltTheme.textStyles.sub,
                    color = SaltTheme.colors.subText,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis
                )
            }
        }
        badge?.let { StatusPill(it, SaltTheme.colors.highlight) }
        if (tab == WorkspaceTab.BACKGROUND) {
            val jobId = obj.string("job_id").ifBlank { obj.string("id") }
            if (jobId.isNotBlank()) {
                Button(
                    onClick = { controller.stopBackgroundTask(jobId) },
                    text = "停止",
                    appearance = ButtonAppearance.Subtle,
                    intent = ButtonIntent.Destructive
                )
            }
        }
        if (tab in setOf(WorkspaceTab.FILES, WorkspaceTab.DELIVERABLES)) {
            val path = obj.string("path").ifBlank { obj.string("rel") }
            val sid = controller.selectedSession?.id
            if (path.isNotBlank() && sid != null) {
                Button(
                    onClick = {
                        controller.openBackendUrl(
                            "/api/sessions/${urlPart(sid)}/artifacts/download?path=${urlPart(path)}"
                        )
                    },
                    text = "下载",
                    appearance = ButtonAppearance.Subtle
                )
            }
        }
    }
}

private fun urlPart(value: String): String =
    URLEncoder.encode(value, StandardCharsets.UTF_8)

private fun extractWorkspaceItems(tab: WorkspaceTab, data: JsonElement): List<JsonElement> {
    if (data is JsonArray) return data.toList()
    val root = data.objectOrEmpty()
    fun arrayOf(vararg names: String): List<JsonElement> =
        names.firstNotNullOfOrNull { name ->
            root.element(name)?.let { value ->
                when (value) {
                    is JsonArray -> value.toList()
                    is JsonObject -> listOf(value)
                    else -> null
                }
            }
        }.orEmpty()

    return when (tab) {
        WorkspaceTab.FILES,
        WorkspaceTab.DELIVERABLES -> arrayOf("items", "deliverables")
        WorkspaceTab.TOPOLOGY -> arrayOf("topology", "dag_nodes", "tasks")
        WorkspaceTab.ASSETS -> arrayOf("observed_evidence", "assets")
        WorkspaceTab.OPPORTUNITIES -> arrayOf("testing_opportunities", "opportunities")
        WorkspaceTab.CHALLENGES -> arrayOf("challenge_progress", "challenges")
        WorkspaceTab.VULNERABILITIES -> arrayOf("items", "vulns")
        WorkspaceTab.FLAGS -> arrayOf("flags", "items")
        WorkspaceTab.EXPERIENCE -> arrayOf("items", "experience")
        WorkspaceTab.TRAFFIC -> arrayOf("records", "sessions", "packets")
        WorkspaceTab.BACKGROUND -> arrayOf("items", "jobs", "tasks")
        WorkspaceTab.EVENTS -> arrayOf("items", "events", "activity")
        WorkspaceTab.CHAT,
        WorkspaceTab.REPORT -> emptyList()
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
private fun ConversationCard(controller: AppController, modifier: Modifier = Modifier) {
    val workspace = controller.workspace
    Card(modifier = modifier.fillMaxHeight(), color = SaltTheme.colors.subBackground) {
        Column(Modifier.fillMaxSize()) {
            Row(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 18.dp, vertical = 14.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text("对话与证据", modifier = Modifier.weight(1f), fontWeight = FontWeight.SemiBold)
                if (controller.workspaceLoading) {
                    Text("同步中…", style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText)
                }
            }
            Box(Modifier.fillMaxWidth().height(1.dp).background(SaltTheme.colors.stroke))
            if (workspace == null) {
                LoadingBlock("正在载入任务状态…")
            } else {
                LazyColumn(
                    modifier = Modifier.weight(1f),
                    contentPadding = androidx.compose.foundation.layout.PaddingValues(18.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    items(workspace.timeline) { item -> TimelineBubble(item) }
                }
            }
            Box(Modifier.fillMaxWidth().height(1.dp).background(SaltTheme.colors.stroke))
            if (controller.pendingUploads.isNotEmpty()) {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 8.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        "附件 ${controller.pendingUploads.size}",
                        style = SaltTheme.textStyles.sub,
                        color = SaltTheme.colors.subText
                    )
                    controller.pendingUploads.take(3).forEach { upload ->
                        StatusPill(upload.name, SaltTheme.colors.highlight)
                    }
                    Button(
                        onClick = controller::clearUploads,
                        text = "清除",
                        appearance = ButtonAppearance.Subtle
                    )
                }
            }
            Row(
                modifier = Modifier.fillMaxWidth().padding(14.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalAlignment = Alignment.Bottom
            ) {
                SpiritIconButton(Icons.Outlined.AttachFile, "添加附件", {
                    val chooser = JFileChooser().apply {
                        isMultiSelectionEnabled = true
                        dialogTitle = "选择会话附件"
                    }
                    if (chooser.showOpenDialog(null) == JFileChooser.APPROVE_OPTION) {
                        controller.addUploads(chooser.selectedFiles.map { it.toPath() })
                    }
                })
                SpiritTextField(
                    value = controller.workspaceDraft,
                    onValueChange = controller::setDraft,
                    placeholder = "给灵枢补充线索，或布置下一步…",
                    modifier = Modifier.weight(1f),
                    singleLine = false
                )
                Button(
                    onClick = controller::sendWorkspaceMessage,
                    text = "发送",
                    enabled = controller.workspaceDraft.isNotBlank() || controller.pendingUploads.isNotEmpty(),
                    leadingIcon = {
                        Icon(Icons.Outlined.Send, null, modifier = Modifier.size(16.dp))
                    }
                )
            }
        }
    }
}

@Composable
private fun TimelineBubble(item: TimelineItem) {
    val color = when (item.kind) {
        TimelineKind.USER -> SaltTheme.colors.highlight
        TimelineKind.MASTER -> SaltTheme.colors.text
        TimelineKind.AGENT -> SaltTheme.colors.warning
        TimelineKind.FINDING -> SaltTheme.colors.success
        TimelineKind.SYSTEM -> SaltTheme.colors.subText
    }
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = if (item.kind == TimelineKind.USER) Arrangement.End else Arrangement.Start
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth(0.82f)
                .clip(RoundedCornerShape(10.dp))
                .background(color.copy(alpha = 0.08f))
                .padding(14.dp)
        ) {
            Text(item.title, style = SaltTheme.textStyles.sub, color = color, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(5.dp))
            Text(item.body, style = SaltTheme.textStyles.paragraph)
        }
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
private fun WorkspaceSidebar(
    controller: AppController,
    tasks: List<WorkTask>,
    stats: WorkspaceStats?,
    modifier: Modifier = Modifier
) {
    Column(modifier.fillMaxHeight(), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        val approvals = controller.workspace?.pendingApprovals.orEmpty()
        val questions = controller.workspace?.pendingQuestions.orEmpty()
        if (approvals.isNotEmpty() || questions.isNotEmpty()) {
            Card(
                Modifier.fillMaxWidth().heightIn(max = 300.dp),
                color = SaltTheme.colors.warning.copy(alpha = 0.08f)
            ) {
                Column(
                    Modifier.fillMaxWidth()
                        .verticalScroll(rememberScrollState())
                        .padding(14.dp),
                    verticalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    Text("需要你决定", fontWeight = FontWeight.SemiBold, color = SaltTheme.colors.warning)
                    approvals.forEach { approval ->
                        Column(
                            Modifier.fillMaxWidth()
                                .clip(RoundedCornerShape(8.dp))
                                .background(SaltTheme.colors.subBackground)
                                .padding(10.dp)
                        ) {
                            Text(approval.action.ifBlank { "高风险动作" }, fontWeight = FontWeight.Medium)
                            Text(
                                approval.purpose.ifBlank { approval.detail.ifBlank { "请核实目标和影响。" } },
                                style = SaltTheme.textStyles.sub,
                                color = SaltTheme.colors.subText
                            )
                            Spacer(Modifier.height(7.dp))
                            Row(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                                Button(
                                    onClick = { controller.decideApproval(approval.id, true) },
                                    text = "允许"
                                )
                                Button(
                                    onClick = { controller.decideApproval(approval.id, false) },
                                    text = "拒绝",
                                    appearance = ButtonAppearance.Subtle,
                                    intent = ButtonIntent.Destructive
                                )
                            }
                        }
                    }
                    questions.forEach { question ->
                        Column(
                            Modifier.fillMaxWidth()
                                .clip(RoundedCornerShape(8.dp))
                                .background(SaltTheme.colors.subBackground)
                                .padding(10.dp)
                        ) {
                            Text(question.question.ifBlank { "智能体提问" }, fontWeight = FontWeight.Medium)
                            Text(
                                question.agent,
                                style = SaltTheme.textStyles.sub,
                                color = SaltTheme.colors.subText
                            )
                            Spacer(Modifier.height(7.dp))
                            if (question.options.isNotEmpty() && !question.multiple) {
                                question.options.forEachIndexed { index, option ->
                                    Box(
                                        Modifier.fillMaxWidth()
                                            .clip(RoundedCornerShape(7.dp))
                                            .clickable { controller.answerQuestion(question.id, index) }
                                            .padding(vertical = 6.dp)
                                    ) {
                                        Text(option, style = SaltTheme.textStyles.sub)
                                    }
                                }
                            }
                            Button(
                                onClick = { controller.answerQuestion(question.id) },
                                text = "自定义答复",
                                appearance = ButtonAppearance.Subtle
                            )
                        }
                    }
                }
            }
        }
        Card(Modifier.fillMaxWidth(), color = SaltTheme.colors.subBackground) {
            Column(Modifier.padding(18.dp)) {
                Text("执行摘要", fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.height(14.dp))
                if (stats == null) {
                    Text("等待状态…", style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText)
                } else {
                    StatPair("任务节点", tasks.size.toString())
                    StatPair("发现", stats.findings.toString())
                    StatPair("漏洞", stats.vulnerabilities.toString())
                    StatPair("Flag", stats.flags.toString())
                    StatPair("待确认", (stats.approvals + stats.questions).toString())
                }
            }
        }
        Card(Modifier.fillMaxWidth().weight(1f), color = SaltTheme.colors.subBackground) {
            Column(Modifier.fillMaxSize().padding(18.dp)) {
                Text("智能体任务", fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.height(12.dp))
                if (tasks.isEmpty()) {
                    Text("尚未派发子任务", style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText)
                } else {
                    LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        items(tasks, key = { it.id }) { task ->
                            Column(
                                Modifier.fillMaxWidth()
                                    .clip(RoundedCornerShape(8.dp))
                                    .background(SaltTheme.colors.background.copy(alpha = 0.55f))
                                    .padding(10.dp)
                            ) {
                                Text(task.title.ifBlank { task.agent }, maxLines = 1, fontWeight = FontWeight.Medium)
                                Text(
                                    "${task.agent} · ${task.status} · ${task.routesDone}/${task.routesTotal} 路线",
                                    style = SaltTheme.textStyles.sub,
                                    color = SaltTheme.colors.subText
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

private data class WorkspaceStats(
    val layers: Map<String, Long>,
    val findings: Int,
    val vulnerabilities: Int,
    val flags: Int,
    val approvals: Int,
    val questions: Int
)

@Composable
private fun StatPair(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 5.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(label, style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText, modifier = Modifier.weight(1f))
        Text(value, style = SaltTheme.textStyles.main, fontWeight = FontWeight.SemiBold)
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
private fun ResourcePage(controller: AppController, section: Section) {
    var query by remember(section) { mutableStateOf("") }
    val items = controller.resources[section].orEmpty()
    val filtered = items.filter {
        query.isBlank() || listOf(it.title, it.subtitle, it.meta, it.status)
            .any { value -> value.contains(query, ignoreCase = true) }
    }
    var selectedId by remember(section, items) { mutableStateOf(items.firstOrNull()?.id) }
    val selected = filtered.firstOrNull { it.id == selectedId } ?: filtered.firstOrNull()
    val error = controller.errors[section]
    val supportsNew = section in setOf(
        Section.SKILLS,
        Section.MCP,
        Section.MODELS,
        Section.PROJECTS,
        Section.SCHEDULES,
        Section.BROWSER_ACCOUNTS
    )
    val supportsEdit = supportsNew || section in setOf(Section.LOCAL_TOOLS, Section.AGENTS)
    val supportsDelete = section in setOf(
        Section.SKILLS,
        Section.MCP,
        Section.MODELS,
        Section.PROJECTS,
        Section.SCHEDULES,
        Section.BROWSER_ACCOUNTS,
        Section.EXPERIENCE
    )
    Row(Modifier.fillMaxSize().padding(26.dp), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        Card(Modifier.weight(1.3f).fillMaxHeight(), color = SaltTheme.colors.subBackground) {
            Column(Modifier.fillMaxSize()) {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(16.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Box(Modifier.weight(1f)) {
                        SpiritTextField(query, { query = it }, "筛选 ${section.title}")
                        Icon(
                            Icons.Outlined.Search,
                            null,
                            modifier = Modifier.align(Alignment.CenterEnd).padding(end = 14.dp).size(17.dp),
                            tint = SaltTheme.colors.subText
                        )
                    }
                    StatusPill("${filtered.size} 项", SaltTheme.colors.highlight)
                    if (supportsNew && section != Section.LOCAL_TOOLS) {
                        Button(
                            onClick = { controller.openResourceAction(section, null, "new") },
                            text = "新建"
                        )
                    }
                    if (section == Section.MODELS) {
                        Button(
                            onClick = controller::openProviderAction,
                            text = "供应商",
                            appearance = ButtonAppearance.Subtle
                        )
                    }
                    if (supportsEdit && (section == Section.LOCAL_TOOLS || selected != null)) {
                        Button(
                            onClick = {
                                controller.openResourceAction(
                                    section,
                                    if (section == Section.LOCAL_TOOLS) null else selected,
                                    "edit"
                                )
                            },
                            text = when (section) {
                                Section.LOCAL_TOOLS -> "配置"
                                Section.AGENTS -> "绑定模型"
                                else -> "编辑"
                            },
                            appearance = ButtonAppearance.Subtle
                        )
                    }
                    if (supportsDelete && selected != null) {
                        Button(
                            onClick = { controller.openResourceAction(section, selected, "delete") },
                            text = "删除",
                            appearance = ButtonAppearance.Subtle,
                            intent = ButtonIntent.Destructive
                        )
                    }
                }
                ResourceExtraActions(controller, section, selected)
                Box(Modifier.fillMaxWidth().height(1.dp).background(SaltTheme.colors.stroke))
                when {
                    error != null -> ErrorBlock(error)
                    controller.loading[section] == true && filtered.isEmpty() -> LoadingBlock()
                    filtered.isEmpty() -> EmptyState("暂无数据", "当前接口没有返回可展示条目。")
                    else -> LazyColumn(
                        contentPadding = androidx.compose.foundation.layout.PaddingValues(10.dp),
                        verticalArrangement = Arrangement.spacedBy(7.dp)
                    ) {
                        items(filtered, key = { it.id.ifBlank { it.title } }) { item ->
                            ResourceRow(
                                item = item,
                                selected = item.id == selected?.id,
                                onClick = { selectedId = item.id }
                            )
                        }
                    }
                }
            }
        }
        ResourceInspector(section, selected, Modifier.weight(0.9f))
    }
}

@Composable
private fun ResourceExtraActions(
    controller: AppController,
    section: Section,
    selected: ResourceItem?
) {
    LazyRow(
        modifier = Modifier.fillMaxWidth(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 16.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(7.dp)
    ) {
        item {
            Row(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                when (section) {
                    Section.ASSETS -> {
                        Button(
                            onClick = controller::openAssetCampaignAction,
                            text = "新建资产采集",
                            appearance = ButtonAppearance.Subtle
                        )
                    }
                    Section.SKILLS -> {
                        Button(
                            onClick = {
                                val chooser = JFileChooser().apply {
                                    dialogTitle = "选择 Skill 压缩包"
                                    fileFilter = javax.swing.filechooser.FileNameExtensionFilter(
                                        "Skill ZIP",
                                        "zip"
                                    )
                                }
                                if (chooser.showOpenDialog(null) == JFileChooser.APPROVE_OPTION) {
                                    controller.importSkillArchive(chooser.selectedFile.toPath())
                                }
                            },
                            text = "导入压缩包",
                            appearance = ButtonAppearance.Subtle
                        )
                    }
                    Section.MODELS -> {
                        if (selected != null) {
                            Button(
                                onClick = { controller.testModel(selected) },
                                text = "测试模型",
                                appearance = ButtonAppearance.Subtle
                            )
                        }
                    }
                    Section.SCHEDULES -> {
                        if (selected != null) {
                            val enabled = runCatching {
                                Json.parseToJsonElement(selected.rawJson).jsonObject["enabled"]
                                    ?.toString()?.toBooleanStrict()
                            }.getOrNull() ?: true
                            Button(
                                onClick = { controller.toggleSchedule(selected.id, !enabled) },
                                text = if (enabled) "停用" else "启用",
                                appearance = ButtonAppearance.Subtle
                            )
                        }
                    }
                    Section.BROWSER_ACCOUNTS -> {
                        if (selected != null) {
                            Button(
                                onClick = { controller.browserAccountAction(selected, "open") },
                                text = "打开登录",
                                appearance = ButtonAppearance.Subtle
                            )
                            Button(
                                onClick = { controller.browserAccountAction(selected, "capture") },
                                text = "保存登录态",
                                appearance = ButtonAppearance.Subtle
                            )
                            Button(
                                onClick = { controller.browserAccountAction(selected, "clear") },
                                text = "清理缓存",
                                appearance = ButtonAppearance.Subtle,
                                intent = ButtonIntent.Destructive
                            )
                        }
                    }
                    Section.LOCAL_TOOLS -> {
                        Button(
                            onClick = { controller.localToolsAction("refresh") },
                            text = "刷新索引",
                            appearance = ButtonAppearance.Subtle
                        )
                        Button(
                            onClick = { controller.localToolsAction("cleanup") },
                            text = "清理暂存",
                            appearance = ButtonAppearance.Subtle,
                            intent = ButtonIntent.Destructive
                        )
                    }
                    Section.PROXIES -> {
                        Button(
                            onClick = { controller.openProxyAction("subscribe") },
                            text = "添加订阅",
                            appearance = ButtonAppearance.Subtle
                        )
                        Button(
                            onClick = { controller.openProxyAction("import") },
                            text = "导入配置",
                            appearance = ButtonAppearance.Subtle
                        )
                        if (selected != null) {
                            Button(
                                onClick = { controller.openProxyAction("plan", selected) },
                                text = "建方案",
                                appearance = ButtonAppearance.Subtle
                            )
                            Button(
                                onClick = { controller.openProxyAction("benchmark", selected) },
                                text = "测速",
                                appearance = ButtonAppearance.Subtle
                            )
                            Button(
                                onClick = { controller.openProxyAction("delete", selected) },
                                text = "删除节点",
                                appearance = ButtonAppearance.Subtle,
                                intent = ButtonIntent.Destructive
                            )
                        }
                    }
                    Section.TRAFFIC -> {
                        if (selected != null) {
                            Button(
                                onClick = { controller.openTrafficSession(selected) },
                                text = "查看会话流量",
                                appearance = ButtonAppearance.Subtle
                            )
                        }
                    }
                    else -> Unit
                }
            }
        }
    }
}

@Composable
private fun ResourceRow(item: ResourceItem, selected: Boolean, onClick: () -> Unit) {
    val background = if (selected) SaltTheme.colors.highlight.copy(alpha = 0.11f) else Color.Transparent
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(9.dp))
            .background(background)
            .clickable(onClick = onClick)
            .padding(horizontal = 13.dp, vertical = 11.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Box(
            Modifier.size(34.dp).clip(RoundedCornerShape(8.dp))
                .background(SaltTheme.colors.highlight.copy(alpha = 0.12f)),
            contentAlignment = Alignment.Center
        ) {
            Text(item.title.take(1).ifBlank { "·" }, color = SaltTheme.colors.highlight, fontWeight = FontWeight.Bold)
        }
        Column(Modifier.weight(1f)) {
            Text(item.title.ifBlank { item.id }, maxLines = 1, overflow = TextOverflow.Ellipsis, fontWeight = FontWeight.Medium)
            Text(
                item.subtitle.ifBlank { item.meta },
                style = SaltTheme.textStyles.sub,
                color = SaltTheme.colors.subText,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
        if (item.status.isNotBlank()) {
            StatusPill(item.status, SaltTheme.colors.highlight)
        }
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
private fun ResourceInspector(section: Section, item: ResourceItem?, modifier: Modifier = Modifier) {
    Card(modifier.fillMaxHeight(), color = SaltTheme.colors.subBackground) {
        if (item == null) {
            EmptyState("选择一项", "在左侧选择 ${section.title} 条目查看详情。")
        } else {
            Column(Modifier.fillMaxSize().padding(20.dp)) {
                Text(item.title.ifBlank { item.id }, style = SaltTheme.textStyles.largeTitle)
                Spacer(Modifier.height(6.dp))
                Text(item.subtitle.ifBlank { item.meta }, style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText)
                Spacer(Modifier.height(16.dp))
                Box(Modifier.fillMaxWidth().height(1.dp).background(SaltTheme.colors.stroke))
                Spacer(Modifier.height(16.dp))
                SelectionContainer {
                    Text(
                        item.rawJson,
                        modifier = Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState()),
                        fontFamily = FontFamily.Monospace,
                        fontSize = 12.sp,
                        lineHeight = 18.sp,
                        color = SaltTheme.colors.text
                    )
                }
            }
        }
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
private fun SettingsPage(controller: AppController) {
    val system = controller.system
    Row(Modifier.fillMaxSize().padding(26.dp), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Card(Modifier.fillMaxWidth(), color = SaltTheme.colors.subBackground) {
                Column(Modifier.padding(20.dp)) {
                    Text("运行状态", fontWeight = FontWeight.SemiBold)
                    Spacer(Modifier.height(16.dp))
                    DetailLine("连接地址", controller.backendUrl)
                    DetailLine("Python 运行时", controller.runtimeName)
                    DetailLine("CPU", "%.1f%%".format(system?.cpu ?: 0.0))
                    DetailLine("内存", "%.1f%%".format(system?.memory ?: 0.0))
                    DetailLine("应用内存", "%.1f MB".format(system?.appMemoryMb ?: 0.0))
                    DetailLine("应用 CPU", "%.2f%%".format(system?.appCpu ?: 0.0))
                }
            }
            Card(Modifier.fillMaxWidth(), color = SaltTheme.colors.subBackground) {
                Column(Modifier.padding(20.dp)) {
                    Text("本地数据", fontWeight = FontWeight.SemiBold)
                    Spacer(Modifier.height(10.dp))
                    Text(
                        "配置、会话数据库和密钥保存在用户目录，不进入程序安装目录。",
                        style = SaltTheme.textStyles.paragraph,
                        color = SaltTheme.colors.subText
                    )
                    Spacer(Modifier.height(16.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        Button(
                            onClick = controller::openDataDirectory,
                            text = "打开数据目录",
                            leadingIcon = {
                                Icon(Icons.Outlined.FolderOpen, null, modifier = Modifier.size(16.dp))
                            }
                        )
                        Button(
                            onClick = controller::openLogFile,
                            text = "打开日志",
                            appearance = ButtonAppearance.Subtle,
                            leadingIcon = {
                                Icon(Icons.Outlined.Description, null, modifier = Modifier.size(16.dp))
                            }
                        )
                    }
                }
            }
            Card(Modifier.fillMaxWidth(), color = SaltTheme.colors.subBackground) {
                Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text("系统维护", fontWeight = FontWeight.SemiBold)
                    Row(horizontalArrangement = Arrangement.spacedBy(9.dp)) {
                        Button(
                            onClick = controller::checkUpdates,
                            text = "检查更新",
                            appearance = ButtonAppearance.Subtle
                        )
                        Button(
                            onClick = controller::loadStorageOverview,
                            text = "存储概览",
                            appearance = ButtonAppearance.Subtle
                        )
                        Button(
                            onClick = controller::openApiConsole,
                            text = "高级 API",
                            appearance = ButtonAppearance.Subtle
                        )
                        Button(
                            onClick = controller::logout,
                            text = "退出登录",
                            appearance = ButtonAppearance.Subtle,
                            intent = ButtonIntent.Destructive
                        )
                    }
                    controller.settingsJson?.let { json ->
                        SelectionContainer {
                            Text(
                                json.toString(),
                                modifier = Modifier.fillMaxWidth().heightIn(max = 180.dp)
                                    .verticalScroll(rememberScrollState()),
                                fontFamily = FontFamily.Monospace,
                                fontSize = 11.sp,
                                color = SaltTheme.colors.subText
                            )
                        }
                    }
                }
            }
        }
        Card(Modifier.weight(1f).fillMaxHeight(), color = SaltTheme.colors.subBackground) {
            Column(Modifier.fillMaxSize().padding(18.dp)) {
                Text("桌面日志", fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.height(10.dp))
                SelectionContainer {
                    Text(
                        controller.logs.takeLast(300).joinToString("\n"),
                        modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()),
                        fontFamily = FontFamily.Monospace,
                        fontSize = 12.sp,
                        lineHeight = 18.sp,
                        color = SaltTheme.colors.subText
                    )
                }
            }
        }
    }
}

@Composable
private fun DetailLine(label: String, value: String) {
    Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText, modifier = Modifier.width(120.dp))
        Text(value.ifBlank { "—" }, style = SaltTheme.textStyles.main, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
fun BootPage(message: String) {
    Column(
        modifier = Modifier.fillMaxSize(),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Box(
            Modifier.size(54.dp).clip(RoundedCornerShape(14.dp))
                .background(SaltTheme.colors.highlight),
            contentAlignment = Alignment.Center
        ) {
            Text("灵", color = SaltTheme.colors.onHighlight, fontSize = 24.sp, fontWeight = FontWeight.Bold)
        }
        Spacer(Modifier.height(18.dp))
        Text("正在启动灵枢", style = SaltTheme.textStyles.largeTitle)
        Spacer(Modifier.height(8.dp))
        Text(message, style = SaltTheme.textStyles.main, color = SaltTheme.colors.subText)
    }
}

@Composable
fun FatalPage(message: String) {
    Column(
        modifier = Modifier.fillMaxSize().padding(40.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Icon(Icons.Outlined.ErrorOutline, null, tint = SaltTheme.colors.error, modifier = Modifier.size(42.dp))
        Spacer(Modifier.height(16.dp))
        Text("灵枢没有启动成功", style = SaltTheme.textStyles.largeTitle)
        Spacer(Modifier.height(10.dp))
        Text(message, style = SaltTheme.textStyles.paragraph, color = SaltTheme.colors.subText)
    }
}

@Composable
private fun sessionStatusColor(session: SessionItem): Color {
    return when {
        session.paused -> SaltTheme.colors.warning
        session.status == "running" -> SaltTheme.colors.highlight
        session.status == "completed" -> SaltTheme.colors.success
        session.status == "failed" -> SaltTheme.colors.error
        session.status == "stopped" -> SaltTheme.colors.subText
        else -> SaltTheme.colors.warning
    }
}

private fun formatNumber(value: Long): String = when {
    value >= 1_000_000 -> "%.2fM".format(value / 1_000_000.0)
    value >= 1_000 -> "%.1fK".format(value / 1_000.0)
    else -> value.toString()
}
