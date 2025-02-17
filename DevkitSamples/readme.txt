# Datafeel DevKit Samples Documentation

This documentation provides an overview of the various sample applications demonstrating the capabilities of the Datafeel DevKit.

## Sample Applications

### 1. Dot Command Sample
Location: DotCommandSample/Program.cs
Features:
- Demonstrates basic dot device control
- Controls LED colors using manual mode
- Reads skin temperature data
- Shows how to handle device communication timeouts
Usage:
- Connects to up to 4 dot devices
- Randomly sets RGB values for each dot's LED
- Reads and displays skin temperature measurements
- Implements proper error handling

### 2. Dot Props Sample
Location: DotPropsSample/Program.cs
Features:
- Shows how to manage dot device properties
- Demonstrates bulk property configuration
- Uses the GlobalManual LED mode
Usage:
- Configures multiple dots simultaneously
- Sets up LED modes and properties for all connected devices
- Provides example of property-based device management

### 3. Low Level API Sample
Location: LowLevelApiSample/Program.cs
Features:
- Demonstrates low-level API usage
- Shows direct device communication
Usage:
- Provides examples of direct device interaction
- Useful for advanced implementations and custom protocols

### 4. Manual Vibration Sample
Location: ManualVibrationSample/Program.cs
Features:
- Controls device vibration manually
- Shows how to set vibration parameters
Usage:
- Demonstrates manual vibration control
- Allows testing different vibration patterns and intensities

### 5. Sequence Vibration Sample
Location: SequenceVibrationSample/Program.cs
Features:
- Creates and plays vibration sequences
- Shows how to chain multiple vibration patterns
Usage:
- Demonstrates programmatic vibration sequence creation
- Shows how to implement complex vibration patterns

### 6. Track Player Sample
Location: TrackPlayerSample/Program.cs
Features:
- Plays predefined tracks from JSON files
- Supports both LED and vibration tracks
Usage:
- Loads track data from JSON files in the Tracks directory
- Demonstrates track playback functionality
- Includes sample tracks: led-track.json, vibe-track.json, my-track.json

## General Setup for All Samples
1. Each sample requires initialization of the DotManager with appropriate device configuration
2. Samples use either Serial or BLE communication
3. Default configuration supports up to 4 dot devices
4. All samples implement proper error handling and resource cleanup

## Common Features Across Samples
- Device connection management
- Error handling and timeout implementation
- Resource cleanup using proper disposal patterns
- Support for multiple dot devices
- Configurable communication parameters

## Command Reference

### Dot Command Sample Commands
```csharp
// Initialize DotManager
var manager = new DotManagerConfiguration()
    .AddDot<Dot_63x_xxx>(1)
    .AddDot<Dot_63x_xxx>(2)
    .AddDot<Dot_63x_xxx>(3)
    .AddDot<Dot_63x_xxx>(4)
    .CreateDotManager();

// Set LED colors
foreach (var d in manager.Dots)
{
    d.LedMode = LedModes.GlobalManual;
    d.GlobalLed.Red = (byte)random.Next(0, 255);
    d.GlobalLed.Green = (byte)random.Next(0, 255);
    d.GlobalLed.Blue = (byte)random.Next(0, 255);
}

// Read skin temperature
await d.Write(writeCancelSource.Token);
await d.Read(readCancelSource.Token);
Console.WriteLine($"Skin Temperature: {d.SkinTemperature}");
```

### Dot Props Sample Commands
```csharp
// Create writable dot properties
var dots = new List<DotPropsWritable>()
{
    new DotPropsWritable() { 
        Address = 1, 
        LedMode = LedModes.GlobalManual, 
        GlobalLed = new() 
    },
    // ... repeat for other dots
};

// Write properties to dots
await manager.WriteDotProps(dots);
```

### Manual Vibration Sample Commands
```csharp
// Set vibration parameters
foreach (var d in manager.Dots)
{
    d.VibrationMode = VibrationModes.Manual;
    d.ManualVibration.Amplitude = 100;  // 0-100
    d.ManualVibration.Frequency = 100;  // Hz
    await d.Write();
}

// Stop vibration
foreach (var d in manager.Dots)
{
    d.VibrationMode = VibrationModes.Manual;
    d.ManualVibration.Amplitude = 0;
    d.ManualVibration.Frequency = 0;
    await d.Write();
}
```

### Track Player Sample Commands
```csharp
// Initialize track player
var trackPlayer = new TrackPlayer(manager);

// Play a track from file
string trackPath = "Tracks/my-track.json";
await trackPlayer.PlayTrack(trackPath);
```

### Common Connection Commands
```csharp
// Serial connection
var serialClient = new DatafeelModbusClientConfiguration()
    .UseWindowsSerialPortTransceiver()
    .CreateClient();

// Start the manager
await manager.Start(new List<DatafeelModbusClient> { serialClient }, cancellationToken);

// Proper cleanup
using (var cts = new CancellationTokenSource(10000))
{
    // Your code here
}
```

### Error Handling Commands
```csharp
try
{
    await d.Write(writeCancelSource.Token);
    await d.Read(readCancelSource.Token);
}
catch (Exception e)
{
    Console.WriteLine(e.Message);
}
```

Note: All commands should be wrapped in proper error handling and resource cleanup code blocks. Timeouts should be configured according to your specific needs.

## Requirements
- Datafeel DevKit hardware
- .NET runtime environment
- Proper USB/Serial or BLE connectivity
- Appropriate permissions for device communication

## Best Practices
1. Always implement proper error handling
2. Use appropriate timeout values for your use case
3. Properly dispose of resources using 'using' statements
4. Test connectivity before sending commands
5. Verify device addresses match your configuration

For more detailed information about specific samples, please refer to the comments within each Program.cs file.
