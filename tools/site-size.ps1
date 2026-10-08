# Publishes the playground and says how much a browser downloads to run it: every file as it
# is, and the same files as a server sends them with gzip or with Brotli. Publishing makes both
# compressed copies beside each file, so nothing here compresses anything; it only adds up.
#
# The figures in docs/CPU.md come from here, and CI adds them up the same way on every push.
#
#   pwsh tools/site-size.ps1
#   dotnet run tools/serve.cs -- ../artifacts/site/wwwroot 5196     # to look at what was published

param([string] $Output = 'artifacts/site')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$site = Join-Path $root $Output

if (Test-Path $site) {
    Remove-Item -Recurse -Force $site -Confirm:$false
}

dotnet publish (Join-Path $root 'src/Fetchline.Web') -c Release -o $site --nologo -v q
if ($LASTEXITCODE -ne 0) {
    throw 'the playground did not publish'
}

$files = Get-ChildItem (Join-Path $site 'wwwroot') -Recurse -File
$plain = $files | Where-Object { $_.Extension -notin '.br', '.gz' }

function Total($extension) {
    ($files | Where-Object { $_.Extension -eq $extension } | Measure-Object Length -Sum).Sum
}

'{0} files: {1:N0} bytes as they are, {2:N0} with gzip, {3:N0} with Brotli' -f `
    $plain.Count, ($plain | Measure-Object Length -Sum).Sum, (Total '.gz'), (Total '.br')

# What is Fetchline's own and what is the .NET runtime it runs on.
$ours = $plain | Where-Object { $_.Name -like 'Fetchline.*' -or $_.DirectoryName -notlike '*_framework*' }
$oursBrotli = ($ours | ForEach-Object { (Get-Item ($_.FullName + '.br')).Length } | Measure-Object -Sum).Sum
'of which Fetchline itself: {0:N0} bytes as they are, {1:N0} with Brotli' -f `
    ($ours | Measure-Object Length -Sum).Sum, $oursBrotli
