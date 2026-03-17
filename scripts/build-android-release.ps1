param(
    [string]$KeystorePath = $env:HOWSITGOING_ANDROID_KEYSTORE_PATH,
    [string]$StorePassword = $env:HOWSITGOING_ANDROID_STORE_PASSWORD,
    [string]$KeyAlias = $env:HOWSITGOING_ANDROID_KEY_ALIAS,
    [string]$KeyPassword = $env:HOWSITGOING_ANDROID_KEY_PASSWORD,
    [string]$VersionName = "1.0.$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())",
    [int]$VersionCode = [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
)

if ([string]::IsNullOrWhiteSpace($KeystorePath) -or -not (Test-Path $KeystorePath)) {
    throw "KeystorePath must point to an existing Android signing keystore."
}

if ([string]::IsNullOrWhiteSpace($StorePassword) -or [string]::IsNullOrWhiteSpace($KeyAlias) -or [string]::IsNullOrWhiteSpace($KeyPassword)) {
    throw "StorePassword, KeyAlias, and KeyPassword are required."
}

$env:HOWSITGOING_ANDROID_KEYSTORE_PATH = $KeystorePath
$env:HOWSITGOING_ANDROID_STORE_PASSWORD = $StorePassword
$env:HOWSITGOING_ANDROID_KEY_ALIAS = $KeyAlias
$env:HOWSITGOING_ANDROID_KEY_PASSWORD = $KeyPassword

dotnet publish "$PSScriptRoot\..\HowsItGoing\HowsItGoing.csproj" `
    -f net9.0-android `
    -c Release `
    -p:AndroidPackageFormat=apk `
    -p:ApplicationDisplayVersion=$VersionName `
    -p:ApplicationVersion=$VersionCode
