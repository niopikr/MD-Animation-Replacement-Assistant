param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'restored' }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskOutput) { throw '输出目录已存在，请换一个新目录，避免覆盖已有项目。' }
$taskManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') | ConvertFrom-Json
foreach ($part in $taskManifest.chunks) {
    $taskPart = Join-Path $PSScriptRoot $part.path
    if (!(Test-Path -LiteralPath $taskPart)) { throw "缺少分卷：$($part.path)" }
    if ((Get-Item -LiteralPath $taskPart).Length -ne $part.size -or (Get-FileHash -Algorithm SHA256 -LiteralPath $taskPart).Hash -ne $part.sha256) { throw "分卷校验失败：$($part.path)" }
}
New-Item -ItemType Directory -Path $taskOutput | Out-Null
$taskZip = Join-Path $taskOutput $taskManifest.archive
$taskStream = [IO.File]::Open($taskZip, [IO.FileMode]::CreateNew)
try {
    foreach ($part in $taskManifest.chunks) {
        $taskInput = [IO.File]::OpenRead((Join-Path $PSScriptRoot $part.path))
        try { $taskInput.CopyTo($taskStream) } finally { $taskInput.Dispose() }
    }
} finally { $taskStream.Dispose() }
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $taskZip).Hash -ne $taskManifest.archiveSha256) { throw '完整ZIP校验失败' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($taskZip,$taskOutput)
$taskFolder = Join-Path $taskOutput $taskManifest.folder
foreach ($file in $taskManifest.files) {
    $taskFile = Join-Path $taskFolder $file.path
    if ((Get-Item -LiteralPath $taskFile).Length -ne $file.size -or (Get-FileHash -Algorithm SHA256 -LiteralPath $taskFile).Hash -ne $file.sha256) { throw "解压文件校验失败：$($file.path)" }
}
Write-Host "完成，50个文件已校验：$taskFolder"
