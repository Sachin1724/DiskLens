<#
Builds DiskLens.exe (the launcher) and DiskLens-Setup.exe (the installer with
the whole app embedded). Uses the C# compiler that ships with .NET Framework,
so there is nothing to install.

    pwsh -File installer\build.ps1
#>
param([switch]$SkipIcon)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$app  = Split-Path -Parent $here
$out  = Join-Path $here 'out'
New-Item -ItemType Directory -Path $out -Force | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found - install the .NET Framework 4 developer files' }

# ----------------------------------------------------------------- the icon ---
$ico = Join-Path $out 'disklens.ico'
if (-not $SkipIcon -or -not (Test-Path $ico)) {
  Add-Type -AssemblyName System.Drawing
  $size = 256
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.TextRenderingHint = 'AntiAliasGridFit'
  $g.Clear([System.Drawing.Color]::Transparent)

  # a lime tile with a dark bite out of it: the treemap, one block removed
  $lime = [System.Drawing.ColorTranslator]::FromHtml('#C6F135')
  $ink  = [System.Drawing.ColorTranslator]::FromHtml('#07070A')
  $violet = [System.Drawing.ColorTranslator]::FromHtml('#6B4BFF')
  $cyan = [System.Drawing.ColorTranslator]::FromHtml('#16D9CF')

  $r = 44
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0, 0, $r, $r, 180, 90)
  $path.AddArc($size - $r, 0, $r, $r, 270, 90)
  $path.AddArc($size - $r, $size - $r, $r, $r, 0, 90)
  $path.AddArc(0, $size - $r, $r, $r, 90, 90)
  $path.CloseFigure()
  $g.FillPath((New-Object System.Drawing.SolidBrush $ink), $path)

  $pad = 30; $gap = 12
  $w = $size - 2 * $pad
  $g.FillRectangle((New-Object System.Drawing.SolidBrush $lime),
    $pad, $pad, [int]($w * 0.56), [int]($w * 0.62))
  $g.FillRectangle((New-Object System.Drawing.SolidBrush $violet),
    [int]($pad + $w * 0.56 + $gap), $pad, [int]($w * 0.44 - $gap), [int]($w * 0.62))
  $g.FillRectangle((New-Object System.Drawing.SolidBrush $cyan),
    $pad, [int]($pad + $w * 0.62 + $gap), $w, [int]($w * 0.38 - $gap))

  $g.Dispose()

  $png = New-Object System.IO.MemoryStream
  $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
  $bytes = $png.ToArray()
  $bmp.Dispose()

  # a single-image .ico holding that PNG (valid since Vista)
  $fs = [IO.File]::Create($ico)
  $bw = New-Object IO.BinaryWriter $fs
  $bw.Write([byte[]]@(0,0, 1,0, 1,0))            # reserved, type=icon, count=1
  $bw.Write([byte]0); $bw.Write([byte]0)          # 0 means 256
  $bw.Write([byte]0); $bw.Write([byte]0)          # palette, reserved
  $bw.Write([uint16]1); $bw.Write([uint16]32)     # planes, bpp
  $bw.Write([uint32]$bytes.Length)
  $bw.Write([uint32]22)                           # offset of the image data
  $bw.Write($bytes)
  $bw.Close(); $fs.Close()
  Write-Host "  icon    $ico" -ForegroundColor DarkGray
}

# ------------------------------------------------------------- the launcher ---
$launcher = Join-Path $out 'DiskLens.exe'
& $csc /nologo /target:winexe /optimize+ /warn:1 `
  /out:$launcher /win32icon:$ico `
  /reference:System.dll /reference:System.Windows.Forms.dll `
  (Join-Path $here 'Launcher.cs') | Out-Null
if ($LASTEXITCODE) { throw 'launcher build failed' }
Write-Host "  launcher $launcher" -ForegroundColor DarkGray

# ------------------------------------------------------------- the installer --
# Everything the app needs rides inside the setup as an embedded resource.
$payload = @(
  (Join-Path $app 'server.ps1'),
  (Join-Path $app 'ui.html'),
  (Join-Path $app 'index.html'),
  (Join-Path $app 'README.md'),
  $launcher,
  $ico
)
foreach ($p in $payload) { if (-not (Test-Path $p)) { throw "payload missing: $p" } }
$resArgs = $payload | ForEach-Object { '/resource:' + $_ + ',payload.' + (Split-Path $_ -Leaf) }

$setup = Join-Path $out 'DiskLens-Setup.exe'
& $csc /nologo /target:winexe /optimize+ /warn:1 `
  /out:$setup /win32icon:$ico `
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  @resArgs (Join-Path $here 'Setup.cs') | Out-Null
if ($LASTEXITCODE) { throw 'setup build failed' }

$mb = [math]::Round((Get-Item $setup).Length / 1MB, 2)
Write-Host "`n  DiskLens-Setup.exe  $mb MB" -ForegroundColor Green
Write-Host "  $setup`n" -ForegroundColor DarkGray
