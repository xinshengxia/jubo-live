# 发布签名更新清单

从干净正式提交构建发布包，核对每个 ZIP 和其 SHA-256 文件。在发布机使用 Sign-UpdateChannel.ps1，传入 Product（cs 或 rust）、Version、PackageDirectory 和 Output。私钥由发布机当前 Windows 用户的 DPAPI 保护；发布目录只应包含公钥、签名清单与公开安装包。首次建立信任时用 Initialize 输出公钥；后续发布须使用与已安装客户端公钥配对的原签名密钥。私钥与独立恢复资料需由维护者安全保管。

用本地脚本验证签名与内容后，将 channel.json 添加到对应 GitHub Release 以支持离线导入。附件上传并验收后，在同一仓库的 updates 分支根目录快进发布同一份 channel.json。主程序只跟随经过签名的正式版本；自动检查不安装，只有用户点击更新才开始替换。
