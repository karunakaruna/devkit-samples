#!/bin/bash
set -e
echo "ðŸ” Running security setup..."

# Remove quarantine and set permissions
echo "1. Removing quarantine attributes..."
xattr -r -d com.apple.quarantine . 2>/dev/null || true

echo "2. Setting permissions..."
chmod +x ./MacTest
find . -name "*.dylib" -type f -exec chmod 755 {} \;

echo "3. Checking codesign..."
codesign -d --verbose=4 ./MacTest 2>/dev/null || echo "Not signed (expected)"

echo "4. Running app with hardened runtime disabled..."
# Try running with various security flags
echo "First attempt..."
./MacTest

if [ True -ne 0 ]; then
    echo "Trying with security disabled..."
    DYLD_INSERT_LIBRARIES="" DYLD_LIBRARY_PATH="." ./MacTest
fi