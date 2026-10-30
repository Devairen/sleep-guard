# Builds SleepGuard with the C# compiler that ships inside Windows (.NET Framework 4.8).
# No SDK, no NuGet, no installs.
$ErrorActionPreference = "Stop"
$root  = Split-Path -Parent $PSScriptRoot
$src   = Join-Path $root "src"
$out   = Join-Path $root "bin"
$csc   = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$wpf   = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF"
$gac   = "C:\Windows\Microsoft.NET\assembly\GAC_MSIL"

function Ref($name) {
  $f = Get-ChildItem (Join-Path $gac $name) -Recurse -Filter "$name.dll" -ErrorAction SilentlyContinue |
       Where-Object { $_.FullName -match 'v4\.0_4\.0\.0\.0' } | Select-Object -First 1
  if ($f) { return $f.FullName }
  $alt = Join-Path $wpf "$name.dll"
  if (Test-Path $alt) { return $alt }
  throw "Assembly not found: $name"
}

$refs = @("PresentationFramework","PresentationCore","WindowsBase","System.Xaml",
          "System.Windows.Forms","System.Drawing","System.Core","System") |
        ForEach-Object { "/r:" + (Ref $_) }

New-Item -ItemType Directory -Force -Path $out | Out-Null
$sources = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }
$exe = Join-Path $out "SleepGuard.exe"
$icon = Join-Path $root "assets\app.ico"

$args = @("/nologo","/noconfig","/target:winexe","/platform:x64","/optimize+","/out:$exe") + $refs
if (Test-Path $icon) { $args += "/win32icon:$icon" }
$args += $sources

& $csc $args
if ($LASTEXITCODE -ne 0) { throw "Compile failed ($LASTEXITCODE)" }
Write-Host "Built: $exe  ($([math]::Round((Get-Item $exe).Length/1KB,1)) KB)"
