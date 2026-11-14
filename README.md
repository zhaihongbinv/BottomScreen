# BottomScreen

Windows 11 x64 小屏显示工具：捕获游戏窗口，将完整画面或居中 4:3 区域拉伸到 24 / 27 寸的 16:9 区域，横向居中、底部对齐。

当前版本：**0.2.13**。输出显示器改为按稳定显示器接口 ID 识别，不再只依赖 `\\.\DISPLAY1` 这类输出序号；每次开始显示和自动恢复前都会重新读取当前真实连接的显示器，记忆的显示器未连接时回退到当前主屏并提示，不会因序号被复用而误选另一块实体屏。自动恢复会记住最近一个游戏的完整 EXE 路径及当时的尺寸、画面来源、缩放和显示器；软件启动后空闲时监听，该游戏窗口切到前台并保持默认 5 秒后自动套用并开始显示。切走窗口会取消倒计时，手动停止会抑制该游戏进程实例直到它退出后重新启动；延迟可在 0–120 秒间调整。支持窗口和全屏窗口化；自动复制原有光标样式，不重映射或转发游戏输入。自动 HDR 选项作用于被捕获的游戏，默认关闭，仅点击“应用设置”时处理；“开始显示”及自动恢复都不查询或写入 HDR，也不检查游戏是否重启。显式更改 HDR 配置后仍可能需要手动重启游戏才会生效，但不阻止捕获。顶部“画面更新 FPS”只统计成功提交的新源帧，排除重复画面、光标与统计文字的缓存重绘；它仍不是面板扫描输出的实测帧率。

- [完整使用说明](work/BottomScreen-AutoCursor/README.md)
- [性能与延迟复核](work/BottomScreen-AutoCursor/性能与延迟复核.md)
- [设计与实现说明](work/BottomScreen-AutoCursor/DESIGN-NOTES.md)
- [第三方组件说明](work/BottomScreen-AutoCursor/THIRD-PARTY-NOTICES.txt)

## 源码位置

当前正式源码保留在 `work/BottomScreen-AutoCursor`。其他工作副本、已编译程序、日志、用户设置和测试截图不纳入 Git。

## 本地构建

在 Windows 上安装 .NET SDK 10.0.403 或同一 feature band 的更新补丁。项目目标运行时仍为 .NET 8；发布为自包含程序，使用者不必另装运行时。

```powershell
pwsh -File ./scripts/build.ps1
```

脚本使用锁定的 NuGet 依赖，生成：

- `outputs/BottomScreen-v<version>/`：独立版本归档。
- `outputs/BottomScreen/`：固定启动路径，保留已有 `settings.json` 和日志。
- `outputs/BottomScreen-v<version>-win-x64.zip`：便携包，内含程序、说明、恢复光标工具、许可证及 SHA256。

更新固定目录前会检查运行实例并备份到 `outputs/.backups/`。运行中的固定目录实例不会被强制结束。已有版本归档如果与新构建不同，脚本会停止；需要先提升项目版本号，避免覆盖历史版本。

若只想试构建而不更新本机固定安装，可指定独立输出根目录；这个根目录内仍会同时生成版本目录和固定目录：

```powershell
pwsh -File ./scripts/build.ps1 -OutputRoot ./work/build-check
```

## GitHub 自动构建

每次 `git push`（包括分支和标签）及手动运行都会触发 **Build Windows ZIP**。Windows runner 使用与本地相同的构建脚本，核验两个目录的版本和 SHA256，并核验 ZIP 内的程序。

进入仓库 **Actions → Build Windows ZIP → 对应运行 → Artifacts** 下载 `BottomScreen-v<version>-win-x64.zip`。ZIP 直接作为构建产物上传，不额外套一层 ZIP，保存 30 天。工作流页面的 Summary 也提供下载链接。

构建完成不代表游戏实测通过。GitHub runner 不具备本机 RX 9070、显示器和游戏环境，因此不会运行需要交互桌面和 GPU 的捕获自测。具体性能边界见性能复核文档。

## 发布约定

本地发布必须同时维护版本归档与固定启动目录，不能用新默认设置覆盖现有设置。详见 [AGENTS.md](AGENTS.md)。CI 在其临时工作区生成相同目录结构，不会操作用户电脑。
