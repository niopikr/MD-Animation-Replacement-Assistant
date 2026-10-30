# MD动画替换助手

Windows 本地工具，用于替换 Yu-Gi-Oh! Master Duel 的召唤动画贴图，尽量沿用原动画骨骼与网格。当前源码版本：1.4.0。

完整运行包（含 SAM、本地预览库与运行依赖）见 [发行版下载与还原](dist/v1.4.0/README.md)。

- 搜索中文卡名，按需从用户选择的游戏目录提取动画素材。
- 逐部件涂抹、SAM 点选/套索/涂选、原部件形状选区，支持加减选与撤销。
- 裁片对齐、缩放、透明替换、保留原部件及中途重做。
- 本地 Rigby2D 动画预览和尾帧参考图层，支持缩放、平移、不透明度调节。
- 导出替换/原始 0000 文件夹与替换后尾帧；高清和对应低清资源同步。
- 显式更新游戏动画卡目录，跳过未变化资源。

## 仓库内容

`part-workflow/` 是本地 ASP.NET Core 服务与浏览器界面；`asset-workbench/` 是资源索引/提取组件；`master-duel-modding/src/Floowan.Core/` 是沿用的 Core 源码。`数据/默认目录.zip` 包含清理过的 518 张卡目录（515 张可编辑、1029 个模型描述），无游戏贴图或原始 Bundle。运行时根据用户自己选择的游戏地址提取资源。

Spine 格式预览使用随源码附带的 Rigby2D（MIT），通过 localhost 加载，不调用在线预览服务，也不依赖 Live2DViewerEX。它是实验性兼容实现，不能保证与游戏渲染完全一致。SAM 推理也在本机运行。

源码目录不包含个人项目、配置、运行日志、开发环境、游戏贴图、数据库或模型权重；完整便携发行包（含 SAM 权重）另存于 dist/v1.4.0。此仓库是当前助手的源码快照，不含本地工作区所有历史程序。

## 构建便携程序

需要 Windows x64、.NET 8 SDK 和 PowerShell。首次还原从 NuGet 获取依赖。

```powershell
pwsh -File ./build.ps1 -Output ./artifacts/MD动画替换助手-v1.4.0
```

如需 SAM，将匹配的 `sam2.1_hiera_tiny.encoder.onnx` 和 `sam2.1_hiera_tiny.decoder.onnx` 放在一个本地目录，然后构建：

```powershell
pwsh -File ./build.ps1 -Output ./artifacts/MD动画替换助手-v1.4.0-sam -SamModels "D:/Models/sam2.1"
```

模型来源：https://huggingface.co/cstr/sam2.1-hiera-tiny-ONNX ，Apache-2.0。缺少模型时可使用手动选区，SAM 不会自动下载。构建脚本拒绝覆盖已有目录；输出包含本地 .NET/ASP.NET 与 ONNX 运行时，不需要用户安装 .NET。分发前在目标 Windows 环境测试，保留输出的整个文件夹。微软 C++ 组件复制自构建机器的 System32；机器缺少对应文件时构建会明确报错。

运行方式与导出安装步骤见 [使用说明](使用说明.md)。构建后的程序需要 Windows、本机浏览器与已下载相应资源的游戏安装。程序不自动修改游戏目录。

## 验证范围

原便携版经过本机独立目录启动、实际本地 SAM、Rigby 预览与导出验证；未在全新 Windows 虚拟机完成验证。历史 Core 全量回归为 414 项中 412 通过、2 项既有失败，不代表本次源码上传重新运行了该测试。

## 来源与许可

Core 基于 https://github.com/AmidoriA/master-duel-modding ，记录的上游基线为 `a0e32e64c74a002b06e0e5ebc5fbe04fad19ca63`，包含本地累计修改。沿用 MIT 许可和原版权声明。其他依赖许可见 `licenses/`、`master-duel-modding/THIRD_PARTY_NOTICES.txt` 与 `part-workflow/web/rigby/UPSTREAM.md`。继承组件说明中的其他桌面功能不表示助手会调用这些功能或自动下载对应模型。
