#Requires -Version 7
<#
.SYNOPSIS
Serves the built WebAssembly head over HTTP so the app can be opened in a browser.
Build first: dotnet build ./HowsItGoing/HowsItGoing.csproj -f net9.0-browserwasm
The bridge (port 5217) must be running; it allow-lists this origin so the browser can call it.
Served over localhost, which counts as a secure context - push notifications need one.
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
import http.server, mimetypes, os
# .wasm / .dat must be served with the right type or the runtime refuses to stream-compile.
mimetypes.add_type('application/wasm', '.wasm')
mimetypes.add_type('application/octet-stream', '.dat')
mimetypes.add_type('application/javascript', '.js')
os.chdir(r'$root')
class H(http.server.SimpleHTTPRequestHandler):
    # HTTP/1.1 for keep-alive. The default HTTP/1.0 opens a fresh TCP connection per file, and the
    # head is ~67 MB across hundreds of files, which is slow enough that RequireJS hits its 7s
    # timeout on AppManifest.js and the page never leaves the Uno loader.
    protocol_version = 'HTTP/1.1'
    def end_headers(self):
        self.send_header('Cache-Control', 'no-store')
        super().end_headers()
    def log_message(self, *a): pass
# Threaded for the same reason - keep-alive is no use if requests still queue behind one another.
http.server.ThreadingHTTPServer.allow_reuse_address = True
with http.server.ThreadingHTTPServer(('127.0.0.1', $Port), H) as httpd:
    httpd.serve_forever()
"@
