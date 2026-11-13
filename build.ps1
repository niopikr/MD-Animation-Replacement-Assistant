param(
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$SamModels,
    [string]$Dotnet = 'dotnet',
    [string]$NuGetConfig,
    [string]$EffectRuntime
)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
if (!$NuGetConfig) { $NuGetConfig = Join-Path $taskRoot 'NuGet.Config' }
$taskTarget = if ([IO.Path]::IsPathRooted($Output)) { [IO.Path]::GetFullPath($Output) } else { [IO.Path]::GetFullPath((Join-Path $taskRoot $Output)) }
if (Test-Path -LiteralPath $taskTarget) { throw '输出目录已存在，请选择新目录。' }
if (!$EffectRuntime) { $EffectRuntime = Join-Path $taskRoot '.effect-runtime' }
if (!(Test-Path -LiteralPath $EffectRuntime)) {
    & (Join-Path $taskRoot 'tools/prepare-effect-runtime.ps1') -Output $EffectRuntime
}
foreach ($name in 'python.exe','python313._pth','site-packages/UnityPy/__init__.py') {
    if (!(Test-Path -LiteralPath (Join-Path $EffectRuntime $name) -PathType Leaf)) { throw "效果动画运行环境缺少 $name" }
}
if ($SamModels) {
    foreach ($name in 'sam2.1_hiera_tiny.encoder.onnx','sam2.1_hiera_tiny.decoder.onnx') {
        if (!(Test-Path -LiteralPath (Join-Path $SamModels $name))) { throw "缺少模型：$name" }
    }
}
foreach ($name in 'vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll','msvcp140_1.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $env:WINDIR "System32/$name"))) { throw "构建机缺少微软 C++ 组件：$name，请先安装微软官方 VC++ x64 Redistributable。" }
}
$taskProject = Join-Path $taskRoot 'part-workflow/PartWorkflow.csproj'
& $Dotnet restore $taskProject -r win-x64 -m:1 --configfile $NuGetConfig
if ($LASTEXITCODE -ne 0) { throw '依赖还原失败' }
& $Dotnet publish $taskProject -c Release -r win-x64 --self-contained true --no-restore -m:1 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false (('-p:PathMap={0}=/src' -f $taskRoot)) -o $taskTarget
if ($LASTEXITCODE -ne 0) { throw '构建失败' }
$taskResources = Join-Path $taskTarget '资源'
New-Item -ItemType Directory -Path $taskResources -Force | Out-Null
foreach ($name in 'web','classdata.tpk') { Move-Item -LiteralPath (Join-Path $taskTarget $name) -Destination $taskResources }
Copy-Item -LiteralPath $EffectRuntime -Destination (Join-Path $taskResources 'effect-runtime') -Recurse
$taskEffectRuntime = Join-Path $taskResources 'effect-runtime'
foreach ($name in 'extract_effects.py','pack_shared_summon.py') {
    Copy-Item -LiteralPath (Join-Path $taskRoot "tools/$name") -Destination $taskEffectRuntime
}
$runtimeRoot = [IO.Path]::GetFullPath($taskEffectRuntime) + [IO.Path]::DirectorySeparatorChar
Get-ChildItem -LiteralPath $taskEffectRuntime -File -Recurse | Where-Object Extension -in '.iobj','.ipdb','.pdb','.pyc' | ForEach-Object {
    if (!$_.FullName.StartsWith($runtimeRoot, [StringComparison]::OrdinalIgnoreCase)) { throw '运行时清理路径越界' }
    Remove-Item -LiteralPath $_.FullName -Force
}
New-Item -ItemType Directory -Path (Join-Path $taskResources 'native') -Force | Out-Null
foreach ($name in 'vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll','msvcp140_1.dll') { Copy-Item -LiteralPath (Join-Path $env:WINDIR "System32/$name") -Destination (Join-Path $taskResources 'native') }
if ($SamModels) {
    $taskModelTarget = Join-Path $taskResources 'models/sam2'
    New-Item -ItemType Directory -Path $taskModelTarget -Force | Out-Null
    foreach ($name in 'sam2.1_hiera_tiny.encoder.onnx','sam2.1_hiera_tiny.decoder.onnx') { Copy-Item -LiteralPath (Join-Path $SamModels $name) -Destination $taskModelTarget }
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'licenses') -Destination (Join-Path $taskTarget '许可') -Recurse
New-Item -ItemType Directory -Path (Join-Path $taskTarget '数据') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot '数据/默认目录.zip') -Destination (Join-Path $taskTarget '数据')
if (Test-Path -LiteralPath (Join-Path $taskRoot '数据/效果目录.json')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot '数据/效果目录.json') -Destination (Join-Path $taskTarget '数据')
}
Copy-Item -LiteralPath (Join-Path $taskRoot '使用说明.md') -Destination $taskTarget
'{"Format":"md-animation-helper-portable-v1","Version":"1.5.6","SeedCards":523}' | Set-Content -LiteralPath (Join-Path $taskTarget 'portable.json') -Encoding UTF8
foreach ($name in 'frames','ThirdParty','CardAssetExtractor.runtimeconfig.json','onnxruntime_providers_shared.lib','onnxruntime.lib','web.config') {
    $taskExtra = [IO.Path]::GetFullPath((Join-Path $taskTarget $name))
    if (!$taskExtra.StartsWith($taskTarget + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '清理路径越界' }
    if (Test-Path -LiteralPath $taskExtra) { Remove-Item -LiteralPath $taskExtra -Recurse -Force }
}
Write-Host "完成：$taskTarget"
