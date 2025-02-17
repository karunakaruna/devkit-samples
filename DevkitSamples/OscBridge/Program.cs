using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Datafeel;
using Datafeel.NET.Serial;
using Rug.Osc;

class Program
{
    private static DotManager? manager;
    private static ManagedDot? currentDot;
    private static OscReceiver? oscReceiver;
    private static volatile bool isRunning = true;
    private static readonly object consoleLock = new();
    private const int OSC_PORT = 9001;
    private const int CONNECTION_TIMEOUT = 60000;

    private static readonly ConcurrentQueue<OscPacket> messageQueue = new();
    private static readonly SemaphoreSlim processingThrottle = new(1);
    private static DateTime lastMessageProcessed = DateTime.MinValue;
    private static readonly TimeSpan minMessageInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan deviceWriteInterval = TimeSpan.FromMilliseconds(50);
    private static DateTime lastDeviceWrite = DateTime.MinValue;

    private class DotState
    {
        public byte Red { get; set; }
        public byte Green { get; set; }
        public byte Blue { get; set; }
        public float VibrationIntensity { get; set; }
        public float VibrationFrequency { get; set; }
        public bool IsDirty { get; set; }
    }

    private static readonly Dictionary<int, DotState> dotStates = new();
    private static readonly object statesLock = new();
    private static readonly TimeSpan frameInterval = TimeSpan.FromSeconds(1.0 / 60.0); // 60 FPS

    static async Task Main(string[] args)
    {
        Console.Title = "Datafeel OSC Bridge";
        PrintHeader();

        try
        {
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                isRunning = false;
            };

            // Initialize Datafeel manager
            LogMessage("Initializing Datafeel devices...", ConsoleColor.Yellow);
            
            // List available COM ports
            var ports = System.IO.Ports.SerialPort.GetPortNames();
            LogMessage("Available COM ports:", ConsoleColor.Yellow);
            if (ports.Length == 0)
            {
                LogMessage("No COM ports found! Please ensure your Datafeel device is connected.", ConsoleColor.Red);
                return;
            }

            foreach (var port in ports)
            {
                LogMessage($"  - {port}", ConsoleColor.White);
            }
            LogMessage("", ConsoleColor.White);

            // Try each COM port
            bool connected = false;
            foreach (var port in ports)
            {
                try
                {
                    LogMessage($"Trying port {port}...", ConsoleColor.Yellow);
                    
                    // Create the client first
                    var serialClient = new DatafeelModbusClientConfiguration()
                        .UseWindowsSerialPortTransceiver()
                        .CreateClient();

                    // Open the client connection with retry
                    LogMessage($"Opening serial connection on {port}...", ConsoleColor.Yellow);
                    await RetryOperation(async () => 
                    {
                        await serialClient.Open();
                    }, maxRetries: 3);
                    
                    // Create and configure the manager
                    manager = new DotManagerConfiguration()
                        .AddDot<Dot_63x_xxx>(1)
                        .AddDot<Dot_63x_xxx>(2)
                        .AddDot<Dot_63x_xxx>(3)
                        .AddDot<Dot_63x_xxx>(4)
                        .CreateDotManager();

                    LogMessage($"Attempting to connect to Datafeel devices on {port}...", ConsoleColor.Yellow);
                    bool result = false;
                    using var cts = new CancellationTokenSource(CONNECTION_TIMEOUT);
                    result = await manager.Start(new List<DatafeelModbusClient> { serialClient }, cts.Token);
                    
                    if (result)
                    {
                        connected = true;
                        LogMessage($"Successfully connected on port {port}!", ConsoleColor.Green);
                        
                        // Set the first available device as current
                        var devices = manager.Dots;
                        if (devices.Any())
                        {
                            currentDot = devices.First();
                            LogMessage($"Selected device {currentDot.Address} as current device", ConsoleColor.Green);
                        }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"Failed to connect on {port}: {ex.Message}", ConsoleColor.Yellow);
                    continue;
                }
            }

            if (!connected)
            {
                LogMessage("Failed to connect to any Datafeel devices. Please check:", ConsoleColor.Red);
                LogMessage("1. Are the devices connected via USB?", ConsoleColor.Red);
                LogMessage("2. Do you have permission to access the COM ports?", ConsoleColor.Red);
                LogMessage("3. Is the correct driver installed?", ConsoleColor.Red);
                return;
            }

            InitializeOsc();

            LogMessage("Supported messages:", ConsoleColor.Yellow);
            LogMessage("/datafeel/led/rgb <r> <g> <b>", ConsoleColor.White);
            LogMessage("/datafeel/vibration/intensity <value>", ConsoleColor.White);
            LogMessage("/datafeel/vibration/frequency <value>", ConsoleColor.White);
            LogMessage("/datafeel/device/select <value>", ConsoleColor.White);
            LogMessage("/datafeel/device/reset", ConsoleColor.White);
            LogMessage("\nPress Ctrl+C to exit", ConsoleColor.Yellow);

            // Initialize states for all dots
            foreach (var dot in manager.Dots)
            {
                dotStates[dot.Address] = new DotState();
                // Initialize device settings
                dot.LedMode = LedModes.GlobalManual;
                dot.VibrationMode = VibrationModes.Manual;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await dot.Write(cts.Token);
                }
                catch (Exception ex)
                {
                    LogMessage($"Failed to initialize device {dot.Address}: {ex.Message}", ConsoleColor.Red);
                }
            }

            // Start message processing task
            _ = Task.Run(ProcessMessages);

            // Start device update loop
            _ = Task.Run(DeviceUpdateLoop);

            // Start status update task
            _ = Task.Run(async () => {
                while (isRunning)
                {
                    PrintStatus();
                    await Task.Delay(500); // Update every 500ms
                }
            });

            // Keep the application running
            while (isRunning)
            {
                await Task.Delay(100);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Unexpected error: {ex.Message}", ConsoleColor.Red);
        }
        finally
        {
            // Cleanup
            isRunning = false;
            await Cleanup();
        }
    }

    private static async Task Cleanup()
    {
        if (oscReceiver != null)
        {
            try
            {
                oscReceiver.Close();
            }
            catch (Exception ex)
            {
                LogMessage($"Error closing OSC receiver: {ex.Message}", ConsoleColor.Red);
            }
        }

        if (manager != null)
        {
            try
            {
                await manager.Stop();
            }
            catch (Exception ex)
            {
                LogMessage($"Error stopping manager: {ex.Message}", ConsoleColor.Red);
            }
        }
    }

    private static async Task ProcessMessages()
    {
        while (isRunning && oscReceiver != null)
        {
            try
            {
                // Add timeout to the receive operation
                var receiveTask = Task.Run(() => 
                {
                    try 
                    {
                        // Check if we can receive
                        if (oscReceiver.State != OscSocketState.Connected)
                            return null;
                            
                        return oscReceiver.Receive();
                    }
                    catch (SocketException)
                    {
                        return null;
                    }
                });

                // Wait for receive with timeout
                if (await Task.WhenAny(receiveTask, Task.Delay(100)) == receiveTask)
                {
                    var packet = await receiveTask;
                    if (packet != null)
                    {
                        messageQueue.Enqueue(packet);
                        await ProcessMessageQueue();
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error receiving message: {ex.Message}", ConsoleColor.Red);
                await Task.Delay(100);
            }
        }
    }

    private static async Task ProcessMessageQueue()
    {
        if (!await processingThrottle.WaitAsync(100)) // Add timeout to prevent deadlock
            return;

        try
        {
            var now = DateTime.UtcNow;
            if ((now - lastMessageProcessed) < minMessageInterval)
            {
                processingThrottle.Release();
                return;
            }

            lastMessageProcessed = now;

            // Process up to 5 messages at once to prevent overload
            int processedCount = 0;
            while (processedCount < 5 && messageQueue.TryDequeue(out var packet))
            {
                try
                {
                    if (packet is OscMessage message)
                    {
                        await ProcessOscMessage(message);
                    }
                    else if (packet is OscBundle bundle)
                    {
                        foreach (var msg in bundle)
                        {
                            if (msg is OscMessage bundleMessage)
                            {
                                await ProcessOscMessage(bundleMessage);
                            }
                        }
                    }
                    processedCount++;
                }
                catch (Exception ex)
                {
                    LogMessage($"Error processing message: {ex.Message}", ConsoleColor.Red);
                }
            }
        }
        finally
        {
            processingThrottle.Release();
        }
    }

    private static async Task ProcessOscMessage(OscMessage message)
    {
        if (manager == null) return;

        try
        {
            // Log all incoming messages
            LogMessage($"Received OSC message: {message.Address} with {message.Count} arguments", ConsoleColor.Yellow);

            if (message.Address == "/datafeel/device/select" && message.Count >= 1)
            {
                if (!int.TryParse(message[0].ToString(), out int deviceId))
                {
                    LogMessage("Invalid device ID received", ConsoleColor.Red);
                    return;
                }

                LogMessage($"▶ Selecting device: {deviceId}", ConsoleColor.Cyan);
                
                var newDot = manager.Dots.FirstOrDefault(d => d.Address == deviceId);
                if (newDot != null)
                {
                    currentDot = newDot;
                    // Make sure we have a state object for this device
                    lock (statesLock)
                    {
                        if (!dotStates.ContainsKey(deviceId))
                        {
                            dotStates[deviceId] = new DotState();
                        }
                    }
                }
                else
                {
                    LogMessage($"Device {deviceId} not found", ConsoleColor.Red);
                }
            }
            else if (message.Address == "/datafeel/led/rgb" && message.Count >= 3 && currentDot != null)
            {
                // Parse RGB values safely - expecting values 0-255
                if (!float.TryParse(message[0].ToString(), out float r) ||
                    !float.TryParse(message[1].ToString(), out float g) ||
                    !float.TryParse(message[2].ToString(), out float b))
                {
                    LogMessage("Invalid RGB values received", ConsoleColor.Red);
                    return;
                }

                LogMessage($"▶ Buffering RGB: {r}, {g}, {b}", ConsoleColor.Cyan);
                
                lock (statesLock)
                {
                    if (dotStates.TryGetValue(currentDot.Address, out var state))
                    {
                        // Values are already 0-255, no need to scale
                        state.Red = (byte)r;
                        state.Green = (byte)g;
                        state.Blue = (byte)b;
                        state.IsDirty = true;
                    }
                }
            }
            else if (message.Address == "/datafeel/vibration/intensity" && message.Count >= 1 && currentDot != null)
            {
                if (!float.TryParse(message[0].ToString(), out float intensity))
                {
                    LogMessage("Invalid intensity value received", ConsoleColor.Red);
                    return;
                }

                LogMessage($"▶ Buffering Intensity: {intensity}", ConsoleColor.Cyan);
                
                lock (statesLock)
                {
                    if (dotStates.TryGetValue(currentDot.Address, out var state))
                    {
                        // Scale intensity from 0-1 to 0-100
                        state.VibrationIntensity = intensity * 100;
                        state.IsDirty = true;
                    }
                }
            }
            else if (message.Address == "/datafeel/vibration/frequency" && message.Count >= 1 && currentDot != null)
            {
                if (!float.TryParse(message[0].ToString(), out float frequency))
                {
                    LogMessage("Invalid frequency value received", ConsoleColor.Red);
                    return;
                }

                LogMessage($"▶ Buffering Frequency: {frequency}", ConsoleColor.Cyan);
                
                lock (statesLock)
                {
                    if (dotStates.TryGetValue(currentDot.Address, out var state))
                    {
                        state.VibrationFrequency = frequency;
                        state.IsDirty = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Error processing OSC message: {ex.Message}", ConsoleColor.Red);
        }
    }

    private static async Task DeviceUpdateLoop()
    {
        while (isRunning)
        {
            try
            {
                var startTime = DateTime.UtcNow;

                // Update all dirty devices
                foreach (var dot in manager.Dots)
                {
                    if (dotStates.TryGetValue(dot.Address, out var state) && state.IsDirty)
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        try
                        {
                            // Apply buffered state
                            dot.GlobalLed.Red = state.Red;
                            dot.GlobalLed.Green = state.Green;
                            dot.GlobalLed.Blue = state.Blue;
                            dot.VibrationIntensity = state.VibrationIntensity;
                            dot.VibrationFrequency = state.VibrationFrequency;

                            await dot.Write(cts.Token);
                            state.IsDirty = false;
                            LogMessage($"Updated device {dot.Address}", ConsoleColor.DarkGreen);
                        }
                        catch (OperationCanceledException)
                        {
                            LogMessage($"Update timed out for device {dot.Address}", ConsoleColor.Red);
                        }
                    }
                }

                // Calculate time to next frame
                var elapsed = DateTime.UtcNow - startTime;
                var delay = frameInterval - elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error in update loop: {ex.Message}", ConsoleColor.Red);
                await Task.Delay(frameInterval);
            }
        }
    }

    private static async Task RetryOperation(Func<Task> operation, int maxRetries = 3)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await operation();
                return; // Success, exit
            }
            catch (TaskCanceledException)
            {
                if (i == maxRetries - 1) throw; // Last attempt
                await Task.Delay(50 * (i + 1)); // Exponential backoff
            }
            catch (Exception ex)
            {
                if (i == maxRetries - 1) throw; // Last attempt
                LogMessage($"Retry {i + 1}/{maxRetries}: {ex.Message}", ConsoleColor.Yellow);
                await Task.Delay(50 * (i + 1)); // Exponential backoff
            }
        }
    }

    private static void PrintHeader()
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Datafeel OSC Bridge v1.0");
        Console.WriteLine("-------------------------");
        Console.ResetColor();
    }

    private static void PrintStatus()
    {
        if (currentDot == null) return;

        lock (consoleLock)
        {
            var currentPos = Console.CursorTop;
            Console.SetCursorPosition(0, Console.WindowHeight - 2);
            Console.Write(new string(' ', Console.WindowWidth)); // Clear the line
            Console.SetCursorPosition(0, Console.WindowHeight - 2);
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write($"Device {currentDot.Address} | RGB: ({currentDot.GlobalLed.Red}, {currentDot.GlobalLed.Green}, {currentDot.GlobalLed.Blue}) | Vibration: {currentDot.VibrationIntensity:F2} @ {currentDot.VibrationFrequency:F2}Hz");
            Console.ResetColor();
            Console.SetCursorPosition(0, currentPos);
        }
    }

    private static void LogMessage(string message, ConsoleColor color = ConsoleColor.White)
    {
        lock (consoleLock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ResetColor();
        }
    }

    private static void InitializeOsc()
    {
        try
        {
            var port = OSC_PORT;
            oscReceiver = new OscReceiver(port);
            oscReceiver.Connect();

            LogMessage($"OSC receiver listening on port {port}", ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to initialize OSC: {ex.Message}", ConsoleColor.Red);
            throw;
        }
    }
}
