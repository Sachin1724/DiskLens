<#
Ship a version: bump, build, tag, upload. Installed copies see it on next launch
and offer "Update now".

    pwsh -File release.ps1 -Version 2.2.0 -Notes "what changed"
#>
param([Parameter(Mandatory)][string]$Version, [string]$Notes = '')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
foreach ($f in 'app/DiskLens.csproj', 'app/bundle-mac.sh') {
  (Get-Content $f -Raw) -replace '(?<=<Version>)[\d.]+|(?<=<string>)\d+\.\d+\.\d+(?=</string>\s*\n\s*<key>CFBundlePackageType)', $Version | Set-Content $f -NoNewline
}
(Get-Content installer/Setup.cs -Raw) -replace '(?<=const string Version = ")[\d.]+', $Version | Set-Content installer/Setup.cs -NoNewline
node check-ui.js; if ($LASTEXITCODE) { throw 'ui checks failed' }
pwsh -NoProfile -File installer/build.ps1
git add -A; git commit -m "release: v$Version"
git tag "v$Version"; git push origin main "v$Version"
gh release create "v$Version" installer/out/DiskLens-Setup.exe --title "DiskLens $Version" --notes $Notes
