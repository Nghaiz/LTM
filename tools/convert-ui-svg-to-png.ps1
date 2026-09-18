param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$EdgePath = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
)

$ErrorActionPreference = 'Stop'
$assetRoot = Join-Path $RepositoryRoot 'ironfront-reborn-ui-pack (1)/assets'
if (-not (Test-Path -LiteralPath $EdgePath)) {
    throw "Microsoft Edge was not found at $EdgePath"
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('ironfront-ui-raster-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

try {
    foreach ($source in Get-ChildItem -LiteralPath $assetRoot -Recurse -File -Filter '*.svg') {
        $svg = Get-Content -Raw -LiteralPath $source.FullName
        $viewBox = [regex]::Match($svg, 'viewBox="\s*[-\d.]+\s+[-\d.]+\s+([\d.]+)\s+([\d.]+)\s*"')
        if (-not $viewBox.Success) { throw "No usable viewBox in $($source.FullName)" }

        $isTintableIcon = $source.Directory.Name -eq 'icons'
        $isHighResolution = $isTintableIcon -or $source.BaseName -eq 'ironfront-symbol'
        $scale = if ($isHighResolution) { 4 } else { 2 }
        $width = [math]::Max(1, [int][math]::Round([double]$viewBox.Groups[1].Value * $scale))
        $height = [math]::Max(1, [int][math]::Round([double]$viewBox.Groups[2].Value * $scale))

        $pagePath = Join-Path $temporaryRoot ($source.BaseName + '-' + [guid]::NewGuid().ToString('N') + '.html')
        if ($isTintableIcon) {
            [xml]$document = $svg
            $commands = [System.Collections.Generic.List[string]]::new()
            $commands.Add("const c=document.querySelector('#icon');const x=c.getContext('2d');x.scale($scale,$scale);x.fillStyle='#fff';x.strokeStyle='#fff';")
            foreach ($node in $document.DocumentElement.ChildNodes) {
                if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
                $strokeWidth = if ($node.HasAttribute('stroke-width')) { $node.GetAttribute('stroke-width') } else { '1' }
                $fill = if ($node.HasAttribute('fill')) { $node.GetAttribute('fill') } else { 'currentColor' }
                $stroke = if ($node.HasAttribute('stroke')) { $node.GetAttribute('stroke') } else { 'none' }
                $commands.Add("x.save();x.lineWidth=$strokeWidth;")
                if ($node.LocalName -eq 'path') {
                    $pathData = ConvertTo-Json $node.GetAttribute('d') -Compress
                    $commands.Add("p=new Path2D($pathData);")
                    if ($fill -ne 'none') { $commands.Add('x.fill(p);') }
                    if ($stroke -ne 'none') { $commands.Add('x.stroke(p);') }
                }
                elseif ($node.LocalName -eq 'circle') {
                    $commands.Add("x.beginPath();x.arc($($node.GetAttribute('cx')),$($node.GetAttribute('cy')),$($node.GetAttribute('r')),0,Math.PI*2);")
                    if ($fill -ne 'none') { $commands.Add('x.fill();') }
                    if ($stroke -ne 'none') { $commands.Add('x.stroke();') }
                }
                elseif ($node.LocalName -eq 'rect') {
                    $rx = if ($node.HasAttribute('rx')) { $node.GetAttribute('rx') } else { '0' }
                    $commands.Add("x.beginPath();x.roundRect($($node.GetAttribute('x')),$($node.GetAttribute('y')),$($node.GetAttribute('width')),$($node.GetAttribute('height')),$rx);")
                    if ($fill -ne 'none') { $commands.Add('x.fill();') }
                    if ($stroke -ne 'none') { $commands.Add('x.stroke();') }
                }
                $commands.Add('x.restore();')
            }
            $page = '<!doctype html><meta charset="utf-8"><style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:transparent}canvas{display:block}</style><canvas id="icon" width="' + $width + '" height="' + $height + '"></canvas><script>let p;' + ($commands -join '') + '</script>'
        }
        else {
            $page = '<!doctype html><meta charset="utf-8"><style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:transparent}svg{display:block;width:100%;height:100%}</style>' + $svg
        }
        Set-Content -LiteralPath $pagePath -Value $page -Encoding utf8NoBOM

        $outputPath = [IO.Path]::ChangeExtension($source.FullName, '.png')
        if (Test-Path -LiteralPath $outputPath) {
            Remove-Item -LiteralPath $outputPath -Force
        }
        $uri = [uri]::new($pagePath).AbsoluteUri
        $arguments = @(
            '--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
            '--run-all-compositor-stages-before-draw', '--virtual-time-budget=1000',
            '--default-background-color=00000000', "--window-size=$width,$height",
            ('--screenshot="' + $outputPath + '"'), $uri
        )
        $process = Start-Process -FilePath $EdgePath -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $outputPath)) {
            throw "Rasterization failed for $($source.FullName) (exit $($process.ExitCode))"
        }

        Write-Output "$($source.Name) -> $([IO.Path]::GetFileName($outputPath)) ${width}x${height}"
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
