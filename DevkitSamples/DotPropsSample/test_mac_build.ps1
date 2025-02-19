# Clean up
Remove-Item -Path "test_dist" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path "test_dist"

# Build for each architecture
$architectures = @("osx-x64", "osx-arm64")
foreach ($arch in $architectures) {
    Write-Host "Building test app for $arch..."
    
    # Clean and publish
    dotnet clean TestApp.csproj
    dotnet publish TestApp.csproj -c Release -r $arch --self-contained true
    
    # Create dist folder
    $distFolder = "test_dist/$arch"
    New-Item -ItemType Directory -Force -Path $distFolder
    
    # Copy files
    Copy-Item "bin/Release/net8.0/$arch/publish/*" $distFolder -Force
    
    # Create test script
    @"
#!/bin/bash
echo "Running test app..."
xattr -d com.apple.quarantine ./TestApp 2>/dev/null || true
chmod +x ./TestApp
./TestApp
"@ | Out-File -FilePath "$distFolder/run_test.sh" -Encoding UTF8 -NoNewline
    
    # Create zip
    Compress-Archive -Path "$distFolder/*" -DestinationPath "test_dist/TestApp-$arch.zip" -Force
}

Write-Host "`nCreated test packages:"
Write-Host "- test_dist/TestApp-osx-arm64.zip (for Apple Silicon Macs)"
Write-Host "- test_dist/TestApp-osx-x64.zip (for Intel Macs)"
