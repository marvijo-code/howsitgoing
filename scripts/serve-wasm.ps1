#Requires -Version 7
<#
.SYNOPSIS
Serves the built WebAssembly head over HTTP so the app can be opened in a browser.
Build first: dotnet build ./HowsItGoing/HowsItGoing.csproj -f net9.0-browserwasm
The bridge (port 5217) must be running; it allows any origin so the browser can call it.
#>
param(
    [int]$Port = 5219,
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$root = Join-Path $repoRoot "HowsItGoing\bin\$Configuration\net9.0-browserwasm\wwwroot"

if (-not (Test-Path $root)) {
    throw "WASM output not found at $root. Build it first: dotnet build ./HowsItGoing/HowsItGoing.csproj -f net9.0-browserwasm"
}

Write-Host "Serving $root on http://localhost:$Port/"
python -c @"
import http.server, socketserver, mimetypes, os
# .wasm / .dat must be served with the right type or the runtime refuses to stream-compile.
mimetypes.add_type('application/wasm', '.wasm')
mimetypes.add_type('application/octet-stream', '.dat')
mimetypes.add_type('application/javascript', '.js')
os.chdir(r'$root')
class H(http.server.SimpleHTTPRequestHandler):
    def end_headers(self):
        self.send_header('Cache-Control', 'no-store')
        super().end_headers()
    def log_message(self, *a): pass
socketserver.TCPServer.allow_reuse_address = True
with socketserver.TCPServer(('127.0.0.1', $Port), H) as httpd:
    httpd.serve_forever()
"@
