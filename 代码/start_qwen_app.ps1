param([switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
# The shared launcher loads the current user's encrypted key through tools.config.
& (Join-Path $PSScriptRoot 'start_local_app.ps1') -NoBrowser:$NoBrowser
