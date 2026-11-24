package com.spiritscout.desktop.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Add
import androidx.compose.material.icons.outlined.AttachFile
import androidx.compose.material.icons.outlined.Close
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.DpSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.DialogWindow
import androidx.compose.ui.window.WindowPosition
import androidx.compose.ui.window.rememberDialogState
import com.moriafly.salt.ui.Button
import com.moriafly.salt.ui.ButtonAppearance
import com.moriafly.salt.ui.Card
import com.moriafly.salt.ui.Icon
import com.moriafly.salt.ui.SaltConfigs
import com.moriafly.salt.ui.SaltTheme
import com.moriafly.salt.ui.Switcher
import com.moriafly.salt.ui.Text
import com.moriafly.salt.ui.UnstableSaltUiApi
import com.spiritscout.desktop.AppController
import com.spiritscout.desktop.model.NewTaskRequest
import javax.swing.JFileChooser

@OptIn(UnstableSaltUiApi::class)
@Composable
fun NewTaskWindow(controller: AppController) {
    if (!controller.showNewTask) return
    DialogWindow(
        onCloseRequest = controller::closeNewTask,
        title = "新建灵枢任务",
        state = rememberDialogState(
            size = DpSize(820.dp, 840.dp),
            position = WindowPosition.Aligned(Alignment.Center)
        ),
        resizable = false
    ) {
        SaltTheme(configs = SaltConfigs.default(isDarkTheme = controller.darkTheme)) {
            Box(Modifier.fillMaxSize().background(SaltTheme.colors.background)) {
                NewTaskContent(controller)
            }
        }
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
private fun NewTaskContent(controller: AppController) {
    var title by remember { mutableStateOf("") }
    var mode by remember { mutableStateOf("auto") }
    var target by remember { mutableStateOf("") }
    var mission by remember { mutableStateOf("") }
    var scope by remember { mutableStateOf("") }
    var model by remember { mutableStateOf("自动") }
    var generateWriteup by remember { mutableStateOf(false) }
    var allowSensitive by remember { mutableStateOf(false) }
    var showAdvanced by remember { mutableStateOf(false) }
    var projectId by remember { mutableStateOf("") }
    var proxyPlanId by remember { mutableStateOf("direct") }
    var assetStrategy by remember { mutableStateOf("edge_first") }
    var excludedAssets by remember { mutableStateOf("") }
    var platform by remember { mutableStateOf("") }
    var flagFormat by remember { mutableStateOf("") }
    var ctfHints by remember { mutableStateOf("") }
    var ctfNote by remember { mutableStateOf("") }
    var contestType by remember { mutableStateOf("") }
    var budgetTokens by remember { mutableStateOf("") }
    var rps by remember { mutableStateOf("10") }
    var rulesYaml by remember { mutableStateOf("") }
    var multipleAnswers by remember { mutableStateOf(false) }
    var noLlm by remember { mutableStateOf(false) }
    var acceptRuleConflicts by remember { mutableStateOf(false) }
    var validation by remember { mutableStateOf<String?>(null) }

    Column(
        modifier = Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(26.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp)
    ) {
        Column {
            Text("下达新任务", style = SaltTheme.textStyles.largeTitle)
            Text(
                "选择执行模式并写清目标、授权范围与期望结果。",
                style = SaltTheme.textStyles.sub,
                color = SaltTheme.colors.subText
            )
        }

        Field("任务名称") {
            SpiritTextField(title, { title = it }, "不填则按目标或任务内容自动命名")
        }

        Field("执行模式") {
            LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                items(
                    listOf(
                        "auto" to "自动判断",
                        "src" to "SRC 渗透",
                        "ctf" to "CTF 解题",
                        "competition" to "智能渗透赛",
                        "awdp" to "AWDP 修补"
                    )
                ) { option ->
                    ChoiceChip(option.second, mode == option.first) { mode = option.first }
                }
            }
        }

        Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
            Field("目标", Modifier.weight(1f)) {
                SpiritTextField(target, { target = it }, "域名、URL、IP 或题目标识")
            }
            Field("授权范围", Modifier.weight(1f)) {
                SpiritTextField(scope, { scope = it }, "每行一个目标；留空时使用目标")
            }
        }

        Field("任务说明") {
            SpiritTextField(
                mission,
                { mission = it },
                "说明业务目标、已知线索、限制条件和期望交付物…",
                singleLine = false
            )
        }

        Field("附件") {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(
                        onClick = {
                            val chooser = JFileChooser().apply {
                                isMultiSelectionEnabled = true
                                dialogTitle = "选择要随任务提交的文件"
                            }
                            if (chooser.showOpenDialog(null) == JFileChooser.APPROVE_OPTION) {
                                controller.addUploads(chooser.selectedFiles.map { it.toPath() })
                            }
                        },
                        text = if (controller.uploading) "正在上传…" else "添加附件",
                        appearance = ButtonAppearance.Subtle,
                        leadingIcon = {
                            Icon(Icons.Outlined.AttachFile, null, modifier = Modifier.padding(1.dp))
                        }
                    )
                    if (controller.pendingUploads.isNotEmpty()) {
                        Text(
                            "已选择 ${controller.pendingUploads.size} 个文件",
                            style = SaltTheme.textStyles.sub,
                            color = SaltTheme.colors.subText,
                            modifier = Modifier.padding(top = 9.dp)
                        )
                    }
                }
                if (controller.pendingUploads.isNotEmpty()) {
                    LazyRow(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                        items(controller.pendingUploads, key = { it.id }) { upload ->
                            Row(
                                modifier = Modifier
                                    .clip(RoundedCornerShape(50))
                                    .background(SaltTheme.colors.subBackground)
                                    .padding(start = 11.dp, end = 5.dp, top = 5.dp, bottom = 5.dp),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.spacedBy(5.dp)
                            ) {
                                Text(upload.name, style = SaltTheme.textStyles.sub)
                                Box(
                                    Modifier.size(20.dp).clip(RoundedCornerShape(50))
                                        .clickable { controller.removeUpload(upload.id) },
                                    contentAlignment = Alignment.Center
                                ) {
                                    Icon(
                                        Icons.Outlined.Close,
                                        "移除附件",
                                        modifier = Modifier.size(13.dp),
                                        tint = SaltTheme.colors.subText
                                    )
                                }
                            }
                        }
                    }
                }
            }
        }

        Field("模型") {
            LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                items(controller.models.ifEmpty { listOf("自动") }) { item ->
                    ChoiceChip(item, model == item) { model = item }
                }
            }
        }

        Button(
            onClick = { showAdvanced = !showAdvanced },
            text = if (showAdvanced) "收起高级选项" else "展开高级选项",
            appearance = ButtonAppearance.Subtle
        )

        if (showAdvanced) {
            Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                Field("项目 ID", Modifier.weight(1f)) {
                    SpiritTextField(projectId, { projectId = it }, "留空则不归入项目")
                }
                Field("代理方案", Modifier.weight(1f)) {
                    SpiritTextField(proxyPlanId, { proxyPlanId = it }, "direct 或 proxy-xxxxxxxxxxxx")
                }
            }
            Field("资产策略") {
                LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    items(
                        listOf(
                            "edge_first" to "边缘优先",
                            "core_first" to "核心优先",
                            "balanced" to "均衡"
                        )
                    ) { option ->
                        ChoiceChip(option.second, assetStrategy == option.first) {
                            assetStrategy = option.first
                        }
                    }
                }
            }
            Field("排除资产") {
                SpiritTextField(
                    excludedAssets,
                    { excludedAssets = it },
                    "每行一个；不测试的域名、网段或资产",
                    singleLine = false
                )
            }
            Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                Field("平台", Modifier.weight(1f)) {
                    SpiritTextField(platform, { platform = it }, "CTF 平台或比赛名称")
                }
                Field("Flag 格式", Modifier.weight(1f)) {
                    SpiritTextField(flagFormat, { flagFormat = it }, "例如 flag{...}")
                }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                Field("题型提示", Modifier.weight(1f)) {
                    SpiritTextField(ctfHints, { ctfHints = it }, "逗号分隔，例如 web,pwn,crypto")
                }
                Field("赛制", Modifier.weight(1f)) {
                    SpiritTextField(contestType, { contestType = it }, "例如 jeopardy / awdp")
                }
            }
            Field("题型补充说明") {
                SpiritTextField(ctfNote, { ctfNote = it }, "可选：说明赛制、规则或特殊要求")
            }
            Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                Field("预算 Token", Modifier.weight(1f)) {
                    SpiritTextField(budgetTokens, { budgetTokens = it }, "留空不设上限")
                }
                Field("RPS", Modifier.weight(1f)) {
                    SpiritTextField(rps, { rps = it }, "默认 10")
                }
            }
            Field("规则 YAML") {
                SpiritTextField(
                    rulesYaml,
                    { rulesYaml = it },
                    "可选：粘贴比赛规则或额外限制",
                    singleLine = false
                )
            }
            Card(Modifier.fillMaxWidth(), color = SaltTheme.colors.subBackground) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                    SwitchLine(
                        title = "允许多个答案",
                        description = "适用于多 Flag、多答案赛题",
                        checked = multipleAnswers
                    ) { multipleAnswers = it }
                    SwitchLine(
                        title = "无模型本地模式",
                        description = "只运行本地工具与审计流程",
                        checked = noLlm
                    ) { noLlm = it }
                    SwitchLine(
                        title = "接受规则冲突",
                        description = "任务发起时直接确认规则冲突",
                        checked = acceptRuleConflicts
                    ) { acceptRuleConflicts = it }
                }
            }
        }

        Card(Modifier.fillMaxWidth(), color = SaltTheme.colors.subBackground) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                SwitchLine(
                    title = "生成解题/测试报告",
                    description = "任务完成后生成可下载报告",
                    checked = generateWriteup
                ) { generateWriteup = it }
                SwitchLine(
                    title = "允许敏感操作",
                    description = "仍需遵守会话授权边界和审批策略",
                    checked = allowSensitive
                ) { allowSensitive = it }
            }
        }

        val error = validation ?: controller.taskError
        if (error != null) {
            Text(error, color = SaltTheme.colors.error, style = SaltTheme.textStyles.sub)
        }

        Spacer(Modifier.height(8.dp))
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.End,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Button(
                onClick = controller::closeNewTask,
                text = "取消",
                appearance = ButtonAppearance.Subtle
            )
            Spacer(Modifier.width(10.dp))
            Button(
                onClick = {
                    if (target.isBlank() && mission.isBlank()) {
                        validation = "至少填写目标或任务说明。"
                        return@Button
                    }
                    validation = null
                    val scopeValues = scope.lines().map { it.trim() }.filter { it.isNotBlank() }
                    controller.createTask(
                        NewTaskRequest(
                            title = title.trim(),
                            mode = mode,
                            target = target.trim(),
                            mission = mission.trim(),
                            scope = scopeValues,
                            model = if (model == "自动") "" else model,
                            generateWriteup = generateWriteup,
                            allowSensitive = allowSensitive,
                            projectId = projectId.trim(),
                            proxyPlanId = proxyPlanId.trim().ifBlank { "direct" },
                            assetStrategy = assetStrategy,
                            excludedAssets = excludedAssets.lines().map { it.trim() }.filter { it.isNotBlank() },
                            platform = platform.trim(),
                            flagFormat = flagFormat.trim(),
                            multipleAnswers = multipleAnswers,
                            ctfTypeHints = ctfHints.split(',', '，').map { it.trim() }.filter { it.isNotBlank() },
                            ctfTypeNote = ctfNote.trim(),
                            rulesYaml = rulesYaml.trim(),
                            contestType = contestType.trim(),
                            budgetTokens = budgetTokens.trim().toLongOrNull(),
                            rps = rps.trim().toDoubleOrNull()?.coerceAtLeast(0.1) ?: 10.0,
                            noLlm = noLlm,
                            acceptRuleConflicts = acceptRuleConflicts
                        )
                    ) { session -> controller.openSession(session) }
                },
                text = "创建并开始",
                leadingIcon = {
                    Icon(Icons.Outlined.Add, null, modifier = Modifier.padding(1.dp))
                }
            )
        }
    }
}

@Composable
private fun Field(
    label: String,
    modifier: Modifier = Modifier,
    content: @Composable () -> Unit
) {
    Column(modifier, verticalArrangement = Arrangement.spacedBy(7.dp)) {
        Text(label, style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText, fontWeight = FontWeight.Medium)
        content()
    }
}

@Composable
private fun ChoiceChip(text: String, selected: Boolean, onClick: () -> Unit) {
    val bg = if (selected) SaltTheme.colors.highlight else SaltTheme.colors.subBackground
    val fg = if (selected) SaltTheme.colors.onHighlight else SaltTheme.colors.text
    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(bg)
            .clickable(onClick = onClick)
            .padding(horizontal = 13.dp, vertical = 8.dp)
    ) {
        Text(text, style = SaltTheme.textStyles.sub, color = fg, fontWeight = FontWeight.Medium)
    }
}

@OptIn(UnstableSaltUiApi::class)
@Composable
private fun SwitchLine(
    title: String,
    description: String,
    checked: Boolean,
    onChange: (Boolean) -> Unit
) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, fontWeight = FontWeight.Medium)
            Text(description, style = SaltTheme.textStyles.sub, color = SaltTheme.colors.subText)
        }
        Switcher(state = checked, onChange = onChange)
    }
}
