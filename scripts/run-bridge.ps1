param(
    [string]$Url = "http://0.0.0.0:5217"
)

$env:ASPNETCORE_URLS = $Url
dotnet run --project "$PSScriptRoot\..\HowsItGoing.Bridge\HowsItGoing.Bridge.csproj" --no-launch-profile
