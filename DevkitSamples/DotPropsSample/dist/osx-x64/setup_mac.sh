#!/bin/bash

# Ensure we're in the right directory
SCRIPT_DIR=$(dirname "$0")
cd "$SCRIPT_DIR" || exit 1

echo "📱 Setting up DataFeel OSC Sample..."

# Remove quarantine attribute from all files
echo "🔒 Removing quarantine attributes..."
xattr -d com.apple.quarantine ./DotPropsSample 2>/dev/null || true
xattr -d com.apple.quarantine ./libSystem.IO.Ports.Native.dylib 2>/dev/null || true
xattr -r -d com.apple.quarantine . 2>/dev/null || true

# Make binaries executable
echo "📋 Making binaries executable..."
chmod +x "./DotPropsSample" || { echo "❌ Failed to make DotPropsSample executable"; exit 1; }
chmod +x "./libSystem.IO.Ports.Native.dylib" || { echo "❌ Failed to make dylib executable"; exit 1; }
echo "✅ Made binaries executable"

# Check for required permissions
echo "🔍 Checking permissions..."

# Check common Bluetooth group names
if ! groups | grep -q "bluetooth"; then
    if ! groups | grep -q "_bluetooth"; then
        echo ""
        echo "⚠️  Bluetooth Permission Required"
        echo "Try these commands in a new terminal window:"
        echo "1. First try: sudo dseditgroup -o edit -a $USER -t user _bluetooth"
        echo "2. If that fails, try: sudo dseditgroup -o edit -a $USER -t user bluetooth"
        echo ""
        echo "If both fail, you may need to:"
        echo "1. Open System Settings"
        echo "2. Go to Bluetooth"
        echo "3. Make sure Bluetooth is turned on"
        echo "4. Allow the app when prompted"
        echo ""
    fi
fi

echo ""
echo "⚠️  IMPORTANT: Security Steps"
echo "1. If macOS tries to move the app to trash, click 'Cancel'"
echo "2. Open System Settings -> Privacy & Security"
echo "3. Look for 'DotPropsSample was blocked'"
echo "4. Click 'Open Anyway'"
echo "5. In the popup, click 'Open'"
echo ""
echo "After security is approved:"
echo "1. Grant Bluetooth access when prompted"
echo "2. Grant USB access when prompted"
echo ""
echo "To run the app, use one of these commands (try them in order):"
echo ""
echo "1. Basic run:"
echo "DATAFEEL_LOG_LEVEL=debug ./DotPropsSample"
echo ""
echo "2. If that crashes, try with more runtime flags:"
echo "DATAFEEL_LOG_LEVEL=debug DOTNET_gcServer=0 DOTNET_TieredCompilation=0 ./DotPropsSample"
echo ""
echo "3. If still crashing, try with trace logging:"
echo "DATAFEEL_LOG_LEVEL=trace DOTNET_gcServer=0 DOTNET_TieredCompilation=0 ./DotPropsSample"
echo ""
echo "4. If none work, try disabling JIT:"
echo "DATAFEEL_LOG_LEVEL=trace DOTNET_gcServer=0 DOTNET_TieredCompilation=0 COMPlus_ReadyToRun=0 COMPlus_ZapDisable=1 ./DotPropsSample"
