param(
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $target) { throw '请选择尚未存在的运行时输出目录。' }
$staging = $target + '.prepare-' + [guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    $zip = Join-Path $staging 'python.zip'
    $runtime = Join-Path $staging 'runtime'
    $wheels = Join-Path $staging 'wheels'
    Invoke-WebRequest -Uri 'https://www.python.org/ftp/python/3.13.17/python-3.13.17-embed-amd64.zip' -OutFile $zip -TimeoutSec 90
    Expand-Archive -LiteralPath $zip -DestinationPath $runtime
    & $Python -m pip --isolated --disable-pip-version-check wheel --no-deps --index-url https://pypi.org/simple --wheel-dir $wheels 'tpk-ar==0.2.4'
    if ($LASTEXITCODE -ne 0) { throw '无法准备 tpk-ar wheel。' }
    & $Python -m pip --isolated --disable-pip-version-check install --no-deps --no-compile --only-binary ':all:' --platform win_amd64 --implementation cp --python-version 3.13 --abi cp313 --index-url https://pypi.org/simple --find-links $wheels --target (Join-Path $runtime 'site-packages') -r (Join-Path $PSScriptRoot 'effect-requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw '效果提取依赖安装失败。' }
    Set-Content -LiteralPath (Join-Path $runtime 'python313._pth') -Value @('python313.zip','.','site-packages') -Encoding ascii
    & (Join-Path $runtime 'python.exe') -B -c 'import UnityPy, PIL, lz4; print("Effect runtime ready")'
    if ($LASTEXITCODE -ne 0) { throw '效果提取依赖加载失败。' }
    Move-Item -LiteralPath $runtime -Destination $target
}
finally {
    $resolved = (Resolve-Path -LiteralPath $staging).ProviderPath
    $item = Get-Item -LiteralPath $resolved -Force
    if ($resolved.Equals($staging,[StringComparison]::OrdinalIgnoreCase) -and !($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
