@echo off
title Datafeel OSC Bridge
setlocal

:: Kill any existing Python and OscBridge processes
taskkill /F /IM python.exe >nul 2>&1
taskkill /F /IM OscBridge.exe >nul 2>&1

:: Set paths
set "SCRIPT_DIR=%~dp0"
set "PYTHON_SERVER=%SCRIPT_DIR%main.py"
set "OSC_BRIDGE=%SCRIPT_DIR%bin\Release\net8.0-windows10.0.22621.0\win-x64\publish\OscBridge.exe"

echo Starting Datafeel OSC Bridge...
echo.

:: Start the C# bridge in a new window
start "Datafeel OSC Bridge" /MIN "%OSC_BRIDGE%"

:: Wait a moment for the bridge to initialize
timeout /t 2 /nobreak >nul

:: Start the Python server
echo Starting Python WebSocket server...
start "Python WebSocket Server" /MIN python "%PYTHON_SERVER%"

:: Wait a moment for the server to initialize
timeout /t 2 /nobreak >nul

echo.
echo Bridge is running!
echo - The interface will open in your default browser
echo - To stop the bridge, close this window
echo.
echo Press any key to stop the bridge...

pause >nul

:: Cleanup when the user presses a key
echo.
echo Stopping servers...
taskkill /F /IM python.exe >nul 2>&1
taskkill /F /IM OscBridge.exe >nul 2>&1
echo Done!

endlocal
