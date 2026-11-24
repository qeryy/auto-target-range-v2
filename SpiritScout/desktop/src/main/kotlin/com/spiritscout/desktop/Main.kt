package com.spiritscout.desktop

import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.width
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.DpSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.WindowPosition
import androidx.compose.ui.window.application
import androidx.compose.ui.window.rememberWindowState
import com.moriafly.salt.ui.SaltConfigs
import com.moriafly.salt.ui.SaltTheme
import com.moriafly.salt.ui.Text
import com.moriafly.salt.ui.UnstableSaltUiApi
import com.moriafly.salt.ui.window.CaptionBarHitTest
import com.moriafly.salt.ui.window.DesktopCaptionBar
import com.moriafly.salt.ui.window.LocalSaltWindowInfo
import com.moriafly.salt.ui.window.SaltWindow
import com.moriafly.salt.ui.window.SaltWindowBackgroundType
import com.moriafly.salt.ui.window.SaltWindowProperties
import com.spiritscout.desktop.ui.SpiritScoutRoot
import kotlinx.coroutines.launch

@OptIn(ExperimentalComposeUiApi::class, UnstableSaltUiApi::class)
fun main() = application {
    val controller = remember { AppController() }
    val closeScope = rememberCoroutineScope()

    LaunchedEffect(Unit) {
        controller.bootstrap()
    }

    SaltTheme(configs = SaltConfigs.default(isDarkTheme = controller.darkTheme)) {
        SaltWindow(
            onCloseRequest = {
                closeScope.launch {
                    controller.shutdown()
                    exitApplication()
                }
            },
            state = rememberWindowState(
                size = DpSize(1420.dp, 900.dp),
                position = WindowPosition.Aligned(Alignment.Center)
            ),
            title = "灵枢 · SpiritScout",
            resizable = true,
            properties = SaltWindowProperties.default(
                captionButtonIsDarkTheme = controller.darkTheme,
                backgroundType = when {
                    SaltWindowBackgroundType.Mica.isSupported() -> SaltWindowBackgroundType.Mica
                    SaltWindowBackgroundType.Vibrancy.isSupported() -> SaltWindowBackgroundType.Vibrancy
                    else -> SaltWindowBackgroundType.None
                },
                backgroundIsDarkTheme = controller.darkTheme
            )
        ) {
            SpiritScoutRoot(
                controller = controller,
                captionBarHeight = LocalSaltWindowInfo.current.captionBarHeight
            )

            DesktopCaptionBar {
                Row(
                    modifier = Modifier.fillMaxSize(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Spacer(Modifier.width(14.dp))
                    Text(
                        if (controller.backendUrl.isBlank()) {
                            "灵枢 · SpiritScout"
                        } else {
                            "灵枢 · SpiritScout  /  ${controller.backendUrl}"
                        }
                    )
                }
            }
            CaptionBarHitTest()
        }
    }
}
