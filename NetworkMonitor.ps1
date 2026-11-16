$ErrorActionPreference = 'Stop'
try {
    $sourcePath = Join-Path $PSScriptRoot 'NetworkMonitor.cs'
    Add-Type -Path $sourcePath -ReferencedAssemblies System.Windows.Forms, System.Drawing, System
    [NetworkMonitor.Program]::Main()
}
catch {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($_.Exception.ToString(), 'Network Monitor Error', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
}
