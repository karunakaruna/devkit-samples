# Datafeel Development Kit Samples Hacking
Jonny Ostrem / Memory

Using Windsurf to generate oscbridge prototypes. So far, no perfect results, but it's working.

Usage: Send data from touchdesigner to port 9001 using the toe here:
DevkitSamples\OscBridge\touchdesigner\osc-bridge.toe



Bridge Prototype 1 -
    - cd into DevkitSamples\OscBridge
    - dotnet restore
    - dotnet build
    - dotnet run    
    
or

    win
        DevkitSamples\OscBridge\start_bridge.bat


Bridge Prototype 2 - 
    - cd into DevkitSamples\DotPropsSample
    - dotnet restore
    - dotnet build
    - dotnet run


win
    DevkitSamples\DotPropsSample\bin\Debug\net8.0-windows10.0.22621.0\DotPropsSample.exe
osx
    DevkitSamples\DotPropsSample\bin\Release\net8.0\osx-x64\publish\DotPropsSample (untested)

    "On the Mac, you'll need to make it executable using the command chmod +x DotPropsSample before running it" 



PortCheck
BLE-v0 and
OscTestSender 

are future tools / not working yet