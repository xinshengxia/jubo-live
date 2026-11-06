# src/Brotli —— B站弹幕 protover=3 解码用 brotli 解码子集

本目录是第三方代码（vendored）与一层本地封层，只用于 `BiliDanmakuClient` 解码 B站弹幕
WebSocket 的 `op=5, ver=3` 帧（帧体是原始 brotli 流，解出的字节是同一套嵌套帧）。
正常路径仍请求 `protover=2`（zlib）；ver=3 是防御性兼容分支。

## 上游与许可

- 项目：[master131/BrotliSharpLib](https://github.com/master131/BrotliSharpLib)（MIT，Copyright (c) 2017 master131）
- 固定 commit：`f0f587c8d7c96a48df04ce1bb80ff77232061e0d`
- 本地只取“解码”子集；来源/许可原文与哈希见 `THIRD_PARTY_NOTICES.md`、`licenses/SOURCES.json`，
  原文在 `licenses/BrotliSharpLib-LICENSE.txt` 与 `licenses/brotli-reference-LICENSE.txt`
  （后者是上游随带的 Google Brotli 参考实现 MIT 原文 `Brotli-LICENSE`）。

## 文件映射（上游路径 → 本地文件）

为了同时满足两条构建路径（`Build-Hardened.ps1` 只收集 `src` 下**一级**子目录里的 `*.cs`；
`live-hub.csproj` 用 `src\Brotli\*.cs`），上游子目录被摊平到本目录；只有两个重名文件改名，
其余文件内容与上游逐字节一致：

| 上游路径 | 本地文件 | 说明 |
| --- | --- | --- |
| `Constants.cs` | `Constants.cs` | 共享常量 |
| `Structures.cs` | `Structures.cs` | 共享结构/委托 |
| `Helpers.cs` | `Helpers.cs` | **有本地补丁**（见下） |
| `Detect.cs` | `Detect.cs` | 端序/CPU 探测 |
| `Decode/BitReader.cs` | `BitReader.cs` | 解码 |
| `Decode/Constants.cs` | `DecodeConstants.cs` | 解码常量（改名，避免与共享同名） |
| `Decode/Decode.cs` | `Decode.cs` | 解码主状态机 |
| `Decode/Huffman.cs` | `Huffman.cs` | 解码 |
| `Decode/State.cs` | `State.cs` | 解码状态生命周期 |
| `Decode/Structures.cs` | `DecodeStructures.cs` | 解码结构（改名，避免与共享同名） |
| `Decode/Tables.cs` | `Tables.cs` | 静态字典/查表（约 903 KiB） |
| `Decode/Transform.cs` | `Transform.cs` | 解码 |
| `Brotli.cs`（仅 DecompressBuffer 的解压循环） | `BiliBrotli.cs` | **本地封层**：分块输出 + 展开预算；去掉压缩门面 |
| `BrotliStream.cs` / `Encode/*` / `Licensed` 根 `Tables.cs` | 未包含 | 仅编码或仅压缩使用 |

逐文件 SHA-256（上游原文与本地文件）可由本说明下方命令复算；只有 `Helpers.cs` 因本地补丁不同。

## 本地补丁（必须保留，否则编译失败或引入额外程序集）

1. `Helpers.cs` 顶部加 `#define NET20`：上游在未定义 `NET20/NET35/NET40` 时用
   `System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned/InitBlockUnaligned`，而 .NET Framework 4.8
   不随带该程序集（离线构建也没有 NuGet 还原）。`NET20` 分支是上游自带的可移植 memcpy/memset。
2. `BiliBrotli.cs` 是本地新写的封层（C# 5 语法），解压循环顺序沿用上游 `Brotli.DecompressBuffer`，
   但输出改 64 KiB 分块并检查 `DanmakuMessageLimits` 展开预算；不解压压缩方向。
3. 上游解码代码本身未做其他修改（含 `nameof`/`=>` 的文件属于未收录的压缩门面，解码子集是 C# 5 可编译的）。

## 编译参数

解码子集含 `unsafe`/`stackalloc`，**必须**：

- `Build-Hardened.ps1`（legacy csc）：`/unsafe`；
- `live-hub.csproj`：`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`。

legacy csc 是 C# 5 编译器；本子集已用 legacy csc /unsafe 实测可编译（编译参数见上）。

## 测试向量生成方式

测试（`tests/BiliBrotliTests.cs`）里的 brotli 压缩常量由本机 Python 生成，命令等价于：

```text
python -c "import brotli,struct,json; body=json.dumps({...},ensure_ascii=False,separators=(',',':')).encode(); inner=struct.pack('>IHHII',16+len(body),16,0,5,1)+body; open('inner.br','wb').write(brotli.compress(inner,quality=4,lgwin=22))"
```

即“先构造一个普通 ver=0 嵌套帧（16 字节大端头 + JSON），再用 brotli 压缩成 ver=3 帧体”。
运行测试不需要 Python/brotli，也不联网。

## 复核命令（只读）

```powershell
Get-ChildItem src/Brotli -Filter *.cs | Get-FileHash -Algorithm SHA256
```
