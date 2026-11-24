package com.spiritscout.desktop.model

data class JsonActionState(
    val title: String,
    val description: String,
    val initialId: String = "",
    val initialJson: String = "{}",
    val method: String,
    val pathTemplate: String,
    val requiresId: Boolean = false,
    val idLabel: String = "唯一标识",
    val reloadSection: Section? = null,
    val navigateOnSuccess: Boolean = false,
    val followUpMethod: String? = null,
    val followUpPathTemplate: String? = null,
    val followUpIdField: String = "",
    val successMessage: String = "操作已完成"
)
