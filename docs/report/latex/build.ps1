# Biên dịch cả 4 báo cáo (hoặc các tệp truyền vào) trên Windows và chuyển PDF ra docs\report\.
#
# Trình biên dịch được chọn theo thứ tự: tectonic đã cài -> xelatex (MiKTeX / TeX Live) đã cài
# -> tectonic cục bộ trong .tools\ (tự tải lần đầu, không cần quyền admin).
#
#   pwsh build.ps1
#   pwsh build.ps1 BaoCao_D_NguyenTuKien_MasterServer.tex
param([string[]]$Files)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$TectonicVersion = '0.17.0'
$ToolsDir = Join-Path $PSScriptRoot '.tools'
$LocalTectonic = Join-Path $ToolsDir 'tectonic.exe'

if (Get-Command tectonic -ErrorAction SilentlyContinue) {
    $Engine = 'tectonic'; $Tectonic = 'tectonic'
} elseif (Get-Command xelatex -ErrorAction SilentlyContinue) {
    $Engine = 'xelatex'
} else {
    if (-not (Test-Path $LocalTectonic)) {
        $url = "https://github.com/tectonic-typesetting/tectonic/releases/download/tectonic%40$TectonicVersion/tectonic-$TectonicVersion-x86_64-pc-windows-msvc.zip"
        Write-Host "Tải Tectonic $TectonicVersion vào .tools\ ..."
        New-Item -ItemType Directory -Force $ToolsDir | Out-Null
        $zip = Join-Path $ToolsDir 'tectonic.zip'
        Invoke-WebRequest $url -OutFile $zip
        Expand-Archive $zip -DestinationPath $ToolsDir -Force
        Remove-Item $zip
    }
    $Engine = 'tectonic'; $Tectonic = $LocalTectonic
}
Write-Host "Trình biên dịch: $Engine"

if (-not $Files) { $Files = Get-ChildItem BaoCao_*.tex | ForEach-Object Name }

foreach ($f in $Files) {
    Write-Host "==> $f"
    if ($Engine -eq 'tectonic') {
        & $Tectonic -X compile $f
    } else {
        xelatex -interaction=nonstopmode -halt-on-error $f | Out-Null
        xelatex -interaction=nonstopmode -halt-on-error $f | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { throw "Biên dịch $f thất bại" }
    Move-Item -Force ([IO.Path]::ChangeExtension($f, '.pdf')) ..\
}
Remove-Item -ErrorAction SilentlyContinue *.aux, *.log, *.toc, *.out
Write-Host 'Xong: PDF nằm trong docs\report\'
