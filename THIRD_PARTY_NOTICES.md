# 第三方来源与许可声明

本程序随包分发或调用了下列第三方组件。完整版权与许可原文保存在仓库的 `licenses/` 目录，并随源码、构建输出和发布包一起分发。

## 随包分发的组件

2026-10-22 的查询页分组、输入框和更多弹幕设置调整未新增外部图标、字体或运行库；继续使用下列固定版本及随包原始许可材料。

侧栏工作台、收藏卡片、历史双播放器操作和本地数据窗口使用本项目的 WinForms / GDI+ 控件；主窗口的按钮、输入框、下拉选择、标签页和状态标签使用 AntdUI 2.4.15（net48 单文件程序集，随包放在启动器同级）。没有引入 ReaLTaiizor、Krypton 或 Lucide 运行库、源码或素材；界面仍使用随包字体，许可原文随 `fonts/`、`licenses/` 携带。界面选型研究和网页预览不作为实际分发依赖。

| 组件 | 用途 | 许可 |
| --- | --- | --- |
| [mpv](https://mpv.io/) | 默认播放器（仅 `full` 包内置） | GPLv2+ / LGPLv2.1+，原文见 `licenses/mpv-LICENSE.GPL.txt`、`licenses/mpv-LICENSE.LGPL.txt` |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | B站取流解析 | 原文见 `licenses/yt-dlp-LICENSE.txt`、`licenses/yt-dlp-THIRD_PARTY_LICENSES.txt` |
| [ZXing.Net](https://github.com/micjahn/ZXing.Net) | 扫码登录的二维码生成 | 原文见 `licenses/ZXing.Net-COPYING.txt` |
| [WebView2 SDK](https://developer.microsoft.com/microsoft-edge/webview2/) | 网页辅助登录（抖音） | 原文见 `licenses/WebView2-SDK-LICENSE.txt`、`licenses/WebView2-SDK-NOTICE.txt` |
| [AntdUI](https://github.com/AntdUI/AntdUI) | 主窗口按钮、输入、下拉选择、标签页与状态标签 | Apache-2.0，原文见 `licenses/AntdUI-LICENSE.txt` |
| 字体资源（`fonts/`） | 弹幕渲染所用字形 | 原文见 `fonts/LICENSE.txt` |

**PotPlayer 不在其中**：它是专有免费软件，其许可不允许再分发。本程序支持调用它，但需要用户自行安装并在界面里指定路径。

## 算法与数据的参考来源

弹幕的接收、协议解析、重连与渲染都在本项目源码中用 C# 实现，不加载任何第三方弹幕客户端程序或弹幕 DLL。部分协议实现与查表参考了下列开源项目，并按各自许可保留了版权与许可原文：

| 项目 | 参考范围 | 许可 |
| --- | --- | --- |
| [blivedm](https://github.com/xfgryujk/blivedm) | B站弹幕、SC、礼物与互动等事件的协议字段与消息模型，包括新版 protobuf 字段编号；未移植 Python 解析代码 | MIT，原文见 `licenses/blivedm-LICENSE.txt` |
| [douyinLive](https://github.com/jwwsjlm/douyinLive) | 抖音 WebSocket 签名实现（适配为 C#） | MIT，原文见 `licenses/douyinLive-LICENSE.txt` |
| [libwebp](https://github.com/webmproject/libwebp) | WebP 无损（VP8L）解码与查表 | BSD 三条款及上游专利授权，原文见 `licenses/libwebp-COPYING.txt`、`licenses/libwebp-PATENTS.txt` |
| [AllLive](https://github.com/xiaoyaocz/AllLive) | 斗鱼弹幕颜色调色表 | MIT，原文见 `licenses/AllLive-LICENSE.txt` |

各参考项目的版权归其原作者所有，随本项目保留原始许可文本。`licenses/SOURCES.json` 记录了上述许可文本的来源快照。

合并后放大、归一化选项及拥挤回退的产品行为参考 [xmcp/pakku.js](https://github.com/xmcp/pakku.js)（作者 xmcp，GPLv3）。本项目另行实现五档放大上限、计数占位和实时碰撞检查，没有移植该扩展的源码、JS/WASM、相似度引擎、公式实现、数据或素材。它不是运行依赖；此项记录产品行为研究来源，不将其代码许可扩大到平台媒体内容。

## 本项目自身的许可

本项目自身采用 **GNU General Public License v3.0**（原文见 [LICENSE](LICENSE)）：可以自由使用、修改、再分发，但再分发衍生作品时必须同样以 GPL-3.0 授权，并提供对应源码。

上表中的第三方组件与参考项目各自保留其原始许可，GPL-3.0 不改变这些条款。分发本项目的二进制时，本项目的对应源码在本仓库；随包分发的第三方组件（如 mpv、yt-dlp）的源码请见其各自上游项目。

## full 包的 mpv 运行时

full 包内置普通 x86_64 构建（不使用 x86_64-v3 变体）。具体版本、对应上游提交及二进制 SHA-256 由 `deps.lock.json` 与包内 `manifest.json` 记录；版权和许可原文对应同一 mpv 上游提交。
