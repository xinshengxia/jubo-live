# 许可与第三方内容

## 原创部分

Fluent Glass 的原创生成器、窗口管理和安装代码、文档、Glass Orbit Logo、放大镜 SVG 按根目录 LICENSE 中的 MIT 许可提供。产品名称中的 Windows、PotPlayer 等名称用于说明兼容性，本项目与相关厂商无隶属关系。

## Lucide

assets/lucide 中的图标来自 lucide-static 0.468.0。保留原始 SVG、来源记录 assets/lucide/SOURCE.txt 和完整 assets/lucide/LICENSE。该许可为 ISC，并保留源自 Feather 的版权说明。它们不因本项目的 MIT 许可而改变许可。

## PotPlayer 默认资源与本地生成结果

公开源码包不包含 Default.dsf、从中提取或修改的兼容资源、生成的 DSF、播放器 EXE/DLL、语言文件或用户偏好。安装器只从用户本机已安装的 PotPlayer 读取这些内容，在本机生成 src/、dist/ 和 .runtime/，这些目录均不纳入新的公开源码仓库。

没有获得或声明获得 PotPlayer 默认资源的额外再分发授权。本地生成 DSF 不代表取得其内含默认资源的公开分发权。发布或分享生成结果之前，需要另行核实其适用条款；本项目的 MIT 许可不授予这些权利。

旧开发仓库历史曾包含默认皮肤资源。因此公开发布应使用 tools/package_source.py 导出的干净目录或源码 ZIP，不能直接推送旧开发仓库历史。导出只采用明确允许的源码目录和文件，不复制 .git，也不复制 src/、dist/、.runtime/、.venv/ 或参考资料。

## 字体与依赖

Windows 字体只从本机读取或交由播放器绘制，不随源码包分发。Pillow 与 resvg-py 由安装器按 requirements.txt 安装，未将它们或其二进制文件打包进公开源码 ZIP；它们分别保留各自的许可证。

## 截图

docs/screenshots 中的图片用于展示 Fluent Glass 在 PotPlayer 中的界面效果；相关产品名称和原生界面元素的权利归各自权利人所有。截图不替代对其中所示第三方资源的授权。
