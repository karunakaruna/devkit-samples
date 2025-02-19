# Clean up any existing processes
Get-Process "DotPropsSample" -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -Path "dist" -Recurse -Force -ErrorAction SilentlyContinue

# Build for each architecture
$architectures = @("osx-x64", "osx-arm64")
foreach ($arch in $architectures) {
    Write-Host "Building for $arch..."
    
    # Clean and publish
    dotnet clean
    dotnet publish -c Release -r $arch --self-contained true /p:PublishSingleFile=false /p:EnableCompressionInSingleFile=false /p:PublishTrimmed=false
    
    # Create dist folder
    $distFolder = "dist/$arch"
    New-Item -ItemType Directory -Force -Path $distFolder
    
    # Copy files
    Copy-Item "bin/Release/net8.0/$arch/publish/*" $distFolder -Force
    Copy-Item "setup_mac.sh" $distFolder -Force
    Copy-Item "DotPropsSample.entitlements" $distFolder -Force
    
    # Create zip (with retries in case of file access issues)
    $maxRetries = 3
    $retryCount = 0
    $success = $false
    
    while (-not $success -and $retryCount -lt $maxRetries) {
        try {
            if (Test-Path "dist/DotPropsSample-$arch.zip") {
                Remove-Item "dist/DotPropsSample-$arch.zip" -Force
            }
            Compress-Archive -Path "$distFolder/*" -DestinationPath "dist/DotPropsSample-$arch.zip" -Force
            $success = $true
        }
        catch {
            $retryCount++
            if ($retryCount -lt $maxRetries) {
                Write-Host "Retrying zip creation... (Attempt $retryCount of $maxRetries)"
                Start-Sleep -Seconds 2
            }
            else {
                Write-Host "Failed to create zip after $maxRetries attempts"
                throw
            }
        }
    }
}

Write-Host "`nCreated distribution packages:"
Write-Host "- dist/DotPropsSample-osx-arm64.zip (for Apple Silicon Macs)"
Write-Host "- dist/DotPropsSample-osx-x64.zip (for Intel Macs)"
