param(
  [switch]$IncludeStandaloneBookshelf
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$clientPath = Join-Path $env:LOCALAPPDATA 'Omyaru Browser\google-drive-client.dat'
if (-not (Test-Path -LiteralPath $clientPath)) {
  $packageClient = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Filter 'google-drive-client.dat' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
  if ($packageClient) { $clientPath = $packageClient }
}
if (-not (Test-Path -LiteralPath $clientPath)) {
  throw 'The Windows desktop Google client is unavailable in this account.'
}

if ($PSVersionTable.PSVersion.Major -lt 6) {
  Add-Type -AssemblyName System.Security
} else {
  Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
}
$entropy = [System.Text.Encoding]::UTF8.GetBytes('omyaru-google-client-v1')
$plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
    [System.IO.File]::ReadAllBytes($clientPath), $entropy,
    [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
$temporary = Join-Path $env:TEMP ('omyaru-google-client-' + [guid]::NewGuid().ToString('N') + '.json')
try {
  $clientId = '136563471424-6phesors0pqgjnpsk3lnb8tr49dh9kvo.apps.googleusercontent.com'
  $secret = [System.Text.Encoding]::UTF8.GetString($plain)
  if ([string]::IsNullOrWhiteSpace($secret)) { throw 'The saved Windows Google client is empty.' }
  $json = @{ installed = @{ client_id = $clientId; client_secret = $secret } } | ConvertTo-Json -Compress -Depth 3
  [System.IO.File]::WriteAllText($temporary, $json, [System.Text.UTF8Encoding]::new($false))
  $secret = $null
  $json = $null
  [Array]::Clear($plain, 0, $plain.Length)

  $desktopVersion = ([xml](Get-Content -Raw -LiteralPath (Join-Path $root 'apps/windows/desktop/PrivacyBrowser.Desktop.csproj'))).Project.PropertyGroup.Version
  $desktopOutput = Join-Path $root "artifacts/releases/Omyaru-v$desktopVersion-win-x64"

  & dotnet publish (Join-Path $root 'apps/windows/desktop/PrivacyBrowser.Desktop.csproj') -c Release -r win-x64 --self-contained true --no-restore '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' "-p:OmyaruGoogleClientJson=$temporary" -o $desktopOutput
  if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
  Write-Output "Desktop: $(Join-Path $desktopOutput 'Omyaru.exe')"

  if ($IncludeStandaloneBookshelf) {
    $bookshelfVersion = ([xml](Get-Content -Raw -LiteralPath (Join-Path $root 'apps/windows/bookshelf/Omyaru.Bookshelf.csproj'))).Project.PropertyGroup.Version
    $bookshelfOutput = Join-Path $root "artifacts/releases/OmyaruBookshelf-v$bookshelfVersion-win-x64"
    & dotnet publish (Join-Path $root 'apps/windows/bookshelf/Omyaru.Bookshelf.csproj') -c Release -r win-x64 --self-contained true --no-restore "-p:OmyaruGoogleClientJson=$temporary" -o $bookshelfOutput
    if ($LASTEXITCODE -ne 0) { throw 'Bookshelf publish failed.' }
    Write-Output "Bookshelf: $(Join-Path $bookshelfOutput 'OmyaruBookshelf.exe')"
  }
}
finally {
  if ($plain) { [Array]::Clear($plain, 0, $plain.Length) }
  if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
