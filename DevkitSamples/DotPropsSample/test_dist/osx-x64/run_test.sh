#!/bin/bash
echo "Running test app..."
xattr -d com.apple.quarantine ./TestApp 2>/dev/null || true
chmod +x ./TestApp
./TestApp