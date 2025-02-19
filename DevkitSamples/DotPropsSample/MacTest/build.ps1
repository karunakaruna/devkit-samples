# Clean up
Remove-Item -Path "dist" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path "dist"

# Build for each architecture
$architectures = @("osx-x64", "osx-arm64")
foreach ($arch in $architectures) {
    Write-Host "Building test app for $arch..."
    
    # Clean and restore first
    dotnet clean
    dotnet restore
    
    # Build with strict runtime settings
    $runtimeConfig = @"
{
  "runtimeOptions": {
    "tfm": "net8.0",
    "framework": {
      "name": "Microsoft.NETCore.App",
      "version": "8.0.0"
    },
    "configProperties": {
      "System.GC.Server": false,
      "System.Runtime.TieredCompilation": false,
      "System.Runtime.InteropServices.BuiltInComInterop": false,
      "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization": false,
      "System.Threading.ThreadPool.MinThreads": 4,
      "System.Threading.ThreadPool.MaxThreads": 25
    }
  }
}
"@
    $runtimeConfig | Out-File -FilePath "MacTest.runtimeconfig.json" -Encoding UTF8
    
    # Build specifically for macOS
    dotnet publish -c Release -r $arch --self-contained true /p:PublishSingleFile=false
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed for $arch"
        continue
    }
    
    # Create dist folder
    $distFolder = "dist/$arch"
    New-Item -ItemType Directory -Force -Path $distFolder
    
    # Copy files
    Copy-Item "bin/Release/net8.0/$arch/publish/*" $distFolder -Force
    Copy-Item "MacTest.entitlements" $distFolder -Force
    Copy-Item "MacTest.runtimeconfig.json" $distFolder -Force
    
    # Create test script with improved security handling
    @"
#!/bin/bash
set -e
echo "🔍 Running security setup..."

# Remove quarantine and set permissions
echo "1. Removing quarantine attributes..."
sudo xattr -r -d com.apple.quarantine . 2>/dev/null || true

echo "2. Setting permissions..."
sudo chmod +x ./MacTest
sudo find . -name "*.dylib" -type f -exec chmod 755 {} \;

echo "3. Setting ownership..."
sudo chown -R root:wheel .

echo "4. Checking codesign..."
codesign -d --verbose=4 ./MacTest 2>/dev/null || echo "Not signed (expected)"

echo "5. Running app with various security configurations..."

echo "Attempt 1: Basic run..."
./MacTest

if [ $? -ne 0 ]; then
    echo "Attempt 2: With security disabled..."
    DYLD_INSERT_LIBRARIES="" DYLD_LIBRARY_PATH="." ./MacTest
fi

if [ $? -ne 0 ]; then
    echo "Attempt 3: With root privileges..."
    sudo DYLD_INSERT_LIBRARIES="" DYLD_LIBRARY_PATH="." ./MacTest
fi
"@ | Out-File -FilePath "$distFolder/run_test.sh" -Encoding UTF8 -NoNewline
    
    # Create zip more carefully
    try {
        Compress-Archive -Path "$distFolder/*" -DestinationPath "dist/MacTest-$arch.zip" -Force
    }
    catch {
        Write-Warning "Failed to create zip for $arch. Error: $_"
    }
}

Write-Host "`nCreated test packages:"
Write-Host "- dist/MacTest-osx-arm64.zip (for Apple Silicon Macs)"
Write-Host "- dist/MacTest-osx-x64.zip (for Intel Macs)"
Write-Host "`nInstructions:"
Write-Host "1. Extract the appropriate zip"
Write-Host "2. Run 'chmod +x run_test.sh'"
Write-Host "3. Run 'sudo ./run_test.sh' (requires admin password)"
