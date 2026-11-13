# MD动画替换助手

Windows 本地工具，用于编辑和回包 Yu-Gi-Oh! Master Duel 的召唤切入与卡片效果素材。当前源码版本 1.5.6。

完整运行包（含 SAM、本地预览库与运行依赖）见 [v1.5.6 发行版](https://github.com/niopikr/MD-Animation-Replacement-Assistant/releases/tag/v1.5.6)。下载并解压 Windows x64 ZIP，双击 `MD动画替换助手.exe`。[更新记录](update.md) 汇总了自 v1.4.0 以来的变化。

- 搜索中文卡名，按需从用户选择的游戏目录提取动画素材。
- 首页可下载所选原动画的 Rigby2D ZIP，包含 JSON、图集和全部 PNG。
- 首页可将修改后的召唤骨架 JSON、atlas 和贴图按所选画质回包，并按游戏原版恢复可判定的 Spine 4.2 约束顺序冲突。
- 逐部件涂抹、SAM 点选/套索/涂选、原部件形状选区，支持加减选与撤销。
- 裁片对齐、缩放、透明替换、保留原部件及中途重做。
- 本地 Rigby2D 动画预览和尾帧参考图层，支持缩放、平移、不透明度调节。
- 导出替换/原始 0000 文件夹与替换后尾帧；高清和对应低清资源同步。
- 显式更新游戏动画卡目录，跳过未变化资源。
- 在首页按卡名提取卡片效果素材，使用 Rigby2D 打开，再将修改后的效果贴图打包为游戏替换文件。更新时同步扫描效果目录。

效果素材提取脚本位于 `tools/extract_effects.py`。更新助手目录后，使用本机 Python 3.11、UnityPy 和 Pillow，读取最新的 `数据/索引/index.json`：

```powershell
python tools/extract_effects.py --index "本机助手目录/数据/索引/index.json" --out "卡片效果动画素材库"
```

可通过 `--only 22692` 限定卡号。脚本生成独立的按卡 ZIP 和中文索引。Rigby2D 文件覆盖可转换的二维 Sprite、骨骼和时间轴片段，3D、粒子及材质由游戏原资源继续播放。

## 仓库内容

`part-workflow/` 是本地 ASP.NET Core 服务与浏览器界面；`asset-workbench/` 是资源索引/提取组件；`master-duel-modding/src/Floowan.Core/` 是沿用的 Core 源码。`数据/默认目录.zip` 包含清理过的 523 张卡目录（520 张可编辑、1040 个模型描述），无游戏贴图或原始 Bundle。运行时根据用户自己选择的游戏地址提取资源。

Spine 格式预览使用随源码附带的 Rigby2D（MIT），通过 localhost 加载，不调用在线预览服务，也不依赖 Live2DViewerEX。它是实验性兼容实现，不能保证与游戏渲染完全一致。SAM 推理也在本机运行。

源码目录包含构建工具和默认目录。完整便携发行包含 SAM 权重，可从 GitHub Releases 下载。

## 构建便携程序

需要 Windows x64、.NET 8 SDK 和 PowerShell。首次构建还需 Python 3.11 或更高版本及 pip，用于准备随发行包提供的效果提取环境。可用 `-EffectRuntime` 指定已通过 `tools/prepare-effect-runtime.ps1` 准备好的目录。发行包可离线运行，用户无需安装 Python。

```powershell
pwsh -File ./build.ps1 -Output ./artifacts/MD动画替换助手-v1.5.6
```

如需 SAM，将匹配的 `sam2.1_hiera_tiny.encoder.onnx` 和 `sam2.1_hiera_tiny.decoder.onnx` 放在一个本地目录，然后构建：

```powershell
pwsh -File ./build.ps1 -Output ./artifacts/MD动画替换助手-v1.5.6-sam -SamModels "D:/Models/sam2.1"
```

模型来源：https://huggingface.co/cstr/sam2.1-hiera-tiny-ONNX ，Apache-2.0。缺少模型时可使用手动选区，SAM 不会自动下载。构建脚本拒绝覆盖已有目录；输出包含本地 .NET/ASP.NET 与 ONNX 运行时，不需要用户安装 .NET。分发前在目标 Windows 环境测试，保留输出的整个文件夹。微软 C++ 组件复制自构建机器的 System32；机器缺少对应文件时构建会明确报错。

运行方式与导出安装步骤见 [使用说明](使用说明.md)。构建后的程序需要 Windows、本机浏览器与已下载相应资源的游戏安装。程序不自动修改游戏目录。

## 验证范围

原便携版经过本机独立目录启动、实际本地 SAM、Rigby 预览与导出验证；未在全新 Windows 虚拟机完成验证。历史 Core 全量回归为 414 项中 412 通过、2 项既有失败，不代表本次源码上传重新运行了该测试。

## 来源与许可

Core 基于 https://github.com/AmidoriA/master-duel-modding ，记录的上游基线为 `a0e32e64c74a002b06e0e5ebc5fbe04fad19ca63`，包含本地累计修改。沿用 MIT 许可和原版权声明。其他依赖许可见 `licenses/`、`master-duel-modding/THIRD_PARTY_NOTICES.txt` 与 `part-workflow/web/rigby/UPSTREAM.md`。继承组件说明中的其他桌面功能不表示助手会调用这些功能或自动下载对应模型。
