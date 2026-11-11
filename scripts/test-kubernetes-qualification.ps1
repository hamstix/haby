$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'test-kubernetes-qualification-regressions.ps1')
& (Join-Path $PSScriptRoot 'test-kubernetes-qualification-evidence.ps1')
