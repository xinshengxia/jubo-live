# jubo-live · 看直播

Windows 绿色版四平台直播启动器：把 **B站 / 虎牙 / 斗鱼 / 抖音** 的直播间，一键交给 **mpv**（也支持 **PotPlayer**）播放，并带上弹幕覆盖层、房间与主播搜索、分平台收藏与最近播放。

免安装：不写注册表、不装服务、不驱动；数据放在 exe 旁边的 `data\` 目录，删掉文件夹就等于卸载。

## 下载

在 [Releases](https://github.com/xinshengxia/jubo-live/releases) 页下载，两个版本功能完全一样，区别只在「带不带播放器」：

| 文件 | 说明 |
| --- | --- |
| `jubo-live-<版本>-win-x64-full.zip` | **自带 mpv**，解压后双击 `看直播.exe` 就能看，推荐 |
| `jubo-live-<版本>-win-x64-lite.zip` | 不带播放器，第一次使用在界面「播放器路径」里指到你自己的 mpv 或 PotPlayer，选一次就记住 |

每个压缩包都附 `.sha256` 校验文件。系统要求：Windows 10/11 64 位。

## 快速上手

1. 解压到任意目录（建议放在你自己有写权限的位置，例如新建的 `jubo-live` 文件夹）。
2. 双击 `看直播.exe`。
3. 选平台 → 填房间号或直接粘贴直播间链接 → 按回车，或点右下角「用 mpv 打开（回车）」。
4. 用的是 lite 包时，先在「播放器路径」点「选择程序…」指定播放器。

## 能做什么

- **四平台一个界面**：B站、虎牙、斗鱼、抖音，房间号、房间别名、完整链接都能直接粘。
- **播放可控**：画质（原画优先 / 高清 / 流畅）× 缓冲（延迟更低 / 播放更稳定），另有一档只对 mpv 生效的「画质增强」。
- **掉流自动恢复**：虎牙 / 斗鱼 / 抖音 走本机中转取流，CDN 掐流会自动重连，播放器侧基本无感。
- **状态看得见**：直播中 / 未开播 / 重播 / 掉流重连，会话列表里一眼能看到；主播下播或房间转到重播态时会明确提示，不会「假装已打开」。
- **弹幕覆盖层**：跟随播放器窗口；双向滚动、固定弹幕、SC 和事件提示共同避让。重复文案合并 `×N` 并适度放大，拥挤时缩小或短暂排队；支持大表情、手工安全区、UID/昵称屏蔽、关键词高亮与置顶。
- **找主播**：按昵称或房间号搜索；候选显示四平台返回的粉丝量，方便区分同名账号；抖音可按抖音号查（`@抖音号`）。
- **登录与收藏**：四平台扫码登录，登录态在取流时自动带上；收藏与最近播放分平台保存。

更细的用法见 [使用说明](使用说明.md)。

## 数据与隐私

- 数据都在 exe 旁边的 `data\`：收藏、最近播放、各平台 Cookie、弹幕设置。
- **发布包不含任何账号数据**：没有收藏、没有历史、没有 Cookie、没有 API Key。
- Cookie 只以明文文件保存在本机，仅用于访问对应平台的公开接口，不会上传到任何第三方服务器。
- 没有遥测、没有统计上报。唯一的可选联网功能是「AI 辅助搜索」，需要你自己设置环境变量 `DEEPSEEK_API_KEY`，不带 key 也能正常用其它所有功能。

## 从源码构建

需要 Windows + .NET Framework 4.8（使用系统自带的编译器，无需安装 Visual Studio）。

```powershell
pwsh -NoProfile -File Build-Hardened.ps1          # 编译 + 离线回归 + 自检
pwsh -NoProfile -File tools\Verify-CleanBuild.ps1 -Workspace .\artifacts\clean-check
```

目录约定：`src/` 应用与弹幕源码、`tests/` 离线用例（无网络、可重复跑）、`tools/` 构建与发布脚本、`lib/` 与 `fonts/` 随包资源、`licenses/` 第三方许可原文。外部依赖（mpv、yt-dlp、ZXing.Net、WebView2 SDK 等）的版本与哈希由 `deps.lock.json` 锁定。

## 许可与声明

- 随包分发的 mpv 采用 GPL/LGPL，许可原文见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 与 `licenses/`。
- **PotPlayer 不随包分发**：它是专有免费软件，其许可不允许再分发；本程序支持调用它，但需要用户自备并在界面里指定路径。
- 本程序只做「本地播放器 + 平台公开页面/接口」的搬运：不破解、不下载、不转播、不提供任何内容源。直播内容版权归各平台与主播所有，请遵守各平台服务条款，仅供个人学习与自用。
- 本项目采用 **GPL-3.0**（见 [LICENSE](LICENSE)）：可以自由使用、修改、再分发，但**再分发衍生作品时必须同样以 GPL-3.0 授权，并提供对应源码**。第三方组件（mpv、yt-dlp 等）各自保留其原始许可，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 与 `licenses/`。

依赖目录通过 `-DependencyRoot` 或 `LIVEHUB_DEPENDENCY_ROOT` 指定，仅在本机构建时使用，不写入公开锁文件或发布清单。打包前和打包后执行隐私检查；发布包只使用面向用户的说明文档。

构建、发布版本以已提交的 `VERSION` 为准；正式交付从干净 HEAD 构建，build ID 保留真实 commit。两条离线构建路径都要通过，超时按失败处理。离线回归不代表真实平台播放和桌面交互已验证。

托管 CI 显式使用 `-Headless`，原生窗口布局/DPI 用例会标为跳过，并在日志列出数量和原因；CI 通过不能替代桌面验收。正式发布默认仍执行本机完整回归和隐藏界面检查。

需要保持前台不受干扰时，发布脚本也可显式使用 `-Headless`：清单会记录原生桌面用例的跳过数量与原因，最终包仍执行隐藏控件检查。此模式不代表原生换屏、鼠标操作或长期观感已经验收。

自用更新与公开发布使用不同入口：

- `Build-Portable.ps1` 更新已有安装，必须显式传入 `-Package`、全新 `-Workspace`、安装目录之外的全新 `-Backup` 和普通 x64 播放器目录 `-MpvDirectory`。先隔离构建验收，再备份并覆盖受控程序文件；保留配置、收藏、历史、Cookie 和 profile，失败时回退文件。
- `tools/Prepare-PublishDependencies.ps1 -Source .\vendor\reference -MpvDirectory .\vendor\mpv-x64 -Out .\artifacts\dependencies` 只把哈希匹配的必要依赖复制到全新目录，不复制个人数据或 PotPlayer。目录参数仅为示例；输入必须具有依赖锁要求的布局与内容。
- `tools/Publish-Release.ps1 -Variant full -DependencyRoot .\artifacts\dependencies -Out .\artifacts\release-full` 生成公开包；lite 同理使用 `-Variant lite` 与独立输出目录。凭据剥离只作用于全新发布 staging。
- 提交和上传前分别检查实际源码、提交范围与身份、最终 ZIP 及嵌套内容、清单、二进制字符串、许可和 CI 日志。候选人工复核；未解决或未覆盖时不上传。ZIP 文件变更后重新生成清单与 SHA-256。附件名为 `jubo-live-<版本>-win-x64-<full|lite>.zip` 和同名 `.zip.sha256`，显示标签留空。
