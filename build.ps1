param(
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$SamModels,
    [string]$Dotnet = 'dotnet',
    [string]$NuGetConfig
)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
if (!$NuGetConfig) { $NuGetConfig = Join-Path $taskRoot 'NuGet.Config' }
$taskTarget = if ([IO.Path]::IsPathRooted($Output)) { [IO.Path]::GetFullPath($Output) } else { [IO.Path]::GetFullPath((Join-Path $taskRoot $Output)) }
if (Test-Path -LiteralPath $taskTarget) { throw '输出目录已存在，请选择新目录。' }
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
Copy-Item -LiteralPath (Join-Path $taskRoot '使用说明.md') -Destination $taskTarget
'{"Format":"md-animation-helper-portable-v1","Version":"1.4.0","SeedCards":518}' | Set-Content -LiteralPath (Join-Path $taskTarget 'portable.json') -Encoding UTF8
foreach ($name in 'frames','ThirdParty','CardAssetExtractor.runtimeconfig.json','onnxruntime_providers_shared.lib','onnxruntime.lib','web.config') {
    $taskExtra = [IO.Path]::GetFullPath((Join-Path $taskTarget $name))
    if (!$taskExtra.StartsWith($taskTarget + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '清理路径越界' }
    if (Test-Path -LiteralPath $taskExtra) { Remove-Item -LiteralPath $taskExtra -Recurse -Force }
}
Write-Host "完成：$taskTarget"
