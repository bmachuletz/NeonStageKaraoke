$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet restore
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
    Write-Host "Fertig: $PSScriptRoot\bin\Release\net10.0\win-x64\publish"
}
finally {
    Pop-Location
}
