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
using System.Text.Json;

class Program
{
    private static DotManager? manager;
    private static Task? updateLoop;
    private static CancellationTokenSource? updateLoopCts;
    private static readonly Dictionary<int, DotState> dotStates = new();
    private static readonly object statesLock = new();
    private static readonly TimeSpan frameInterval = TimeSpan.FromSeconds(1.0 / 60.0); // 60 FPS
    private static readonly TimeSpan deviceTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly ConcurrentQueue<OscPacket> messageQueue = new();
    private static readonly SemaphoreSlim processingThrottle = new(1);
    private static readonly TimeSpan messageProcessInterval = TimeSpan.FromMilliseconds(1); // Process messages as fast as possible
    private static DateTime lastMessageProcessed = DateTime.MinValue;
    private static readonly int MAX_QUEUE_SIZE = 100; // Prevent queue from growing too large
    private static OscReceiver? oscReceiver;
    private static volatile bool isRunning = true;
    private static readonly object consoleLock = new();
    private const int OSC_PORT = 9001;
    private const int CONNECTION_TIMEOUT = 60000;

    private class DotState
    {
        public byte[] RGB { get; set; } = new byte[3];
        public float Vibration { get; set; }
        public float Frequency { get; set; }
        public bool UpdatePending { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    private class DeviceState
    {
        public int[] RGB { get; set; } = new int[3];
        public float Vibration { get; set; }
        public float Frequency { get; set; }
    }

    private class BatchUpdate
    {
        public double Timestamp { get; set; }
        public Dictionary<string, DeviceState> Devices { get; set; } = new();
    }

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
                            var currentDot = devices.First();
                            LogMessage($"Selected device {currentDot.Address} as current device", ConsoleColor.Green);
                            
                            // Initialize state tracking for all devices
                            foreach (var dot in manager.Dots)
                            {
                                dotStates[dot.Address] = new DotState();
                            }
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
            LogMessage("/datafeel/batch_update <json>", ConsoleColor.White);
            LogMessage("\nPress Ctrl+C to exit", ConsoleColor.Yellow);

            // Start device update loop
            updateLoopCts = new CancellationTokenSource();
            updateLoop = StartDeviceUpdateLoop();

            // Start message processing task
            _ = Task.Run(ProcessMessages);

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
        if (!await processingThrottle.WaitAsync(1)) // Use very short timeout
            return;

        try
        {
            var now = DateTime.UtcNow;
            if ((now - lastMessageProcessed) < messageProcessInterval)
            {
                return;
            }

            lastMessageProcessed = now;

            // Drop messages if queue gets too large to prevent memory issues
            while (messageQueue.Count > MAX_QUEUE_SIZE)
            {
                messageQueue.TryDequeue(out _);
                LogMessage("Warning: Message queue overflow, dropping old messages", ConsoleColor.Yellow);
            }

            // Process all pending messages
            while (messageQueue.TryDequeue(out var packet))
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

            if (message.Address == "/datafeel/batch_update" && message.Count >= 1)
            {
                var jsonData = message[0].ToString();
                if (jsonData == null)
                {
                    LogMessage("Invalid JSON data received", ConsoleColor.Red);
                    return;
                }

                LogMessage($"Received JSON: {jsonData}", ConsoleColor.Yellow);

                try 
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };

                    var batchUpdate = JsonSerializer.Deserialize<BatchUpdate>(jsonData, options);
                    
                    if (batchUpdate?.Devices == null)
                    {
                        LogMessage("Invalid batch update format", ConsoleColor.Red);
                        return;
                    }

                    LogMessage($"Parsed {batchUpdate.Devices.Count} device updates", ConsoleColor.Green);

                    // Update device states
                    lock (statesLock)
                    {
                        foreach (var deviceKvp in batchUpdate.Devices)
                        {
                            if (!int.TryParse(deviceKvp.Key, out int deviceId))
                            {
                                LogMessage($"Invalid device ID: {deviceKvp.Key}", ConsoleColor.Red);
                                continue;
                            }

                            var state = deviceKvp.Value;
                            LogMessage($"Device {deviceId}: RGB({state.RGB[0]}, {state.RGB[1]}, {state.RGB[2]}) Vib:{state.Vibration:F2} Freq:{state.Frequency:F2}", ConsoleColor.Cyan);
                            
                            // Get or create device state
                            if (!dotStates.TryGetValue(deviceId, out var dotState))
                            {
                                dotState = new DotState();
                                dotStates[deviceId] = dotState;
                            }

                            // Update state
                            dotState.RGB[0] = (byte)state.RGB[0];
                            dotState.RGB[1] = (byte)state.RGB[1];
                            dotState.RGB[2] = (byte)state.RGB[2];
                            dotState.Vibration = state.Vibration;
                            dotState.Frequency = state.Frequency;
                            dotState.UpdatePending = true;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    LogMessage($"JSON parsing error: {ex.Message}", ConsoleColor.Red);
                    LogMessage($"Raw JSON: {jsonData}", ConsoleColor.Yellow);
                }
                catch (Exception ex)
                {
                    LogMessage($"Error processing batch update: {ex.Message}", ConsoleColor.Red);
                }
            }
            else if (message.Address == "/datafeel/device/select" && message.Count >= 1)
            {
                if (!int.TryParse(message[0].ToString(), out int deviceId))
                {
                    LogMessage("Invalid device ID received", ConsoleColor.Red);
                    return;
                }

                LogMessage($" Selecting device: {deviceId}", ConsoleColor.Cyan);
                
                var newDot = manager.Dots.FirstOrDefault(d => d.Address == deviceId);
                if (newDot != null)
                {
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
            else if (message.Address == "/datafeel/led/rgb" && message.Count >= 3)
            {
                // Parse RGB values safely - expecting floating point values
                if (!float.TryParse(message[0].ToString(), out float r) ||
                    !float.TryParse(message[1].ToString(), out float g) ||
                    !float.TryParse(message[2].ToString(), out float b))
                {
                    LogMessage("Invalid RGB values received", ConsoleColor.Red);
                    return;
                }

                // Round and clamp values to 0-255 range
                byte rByte = (byte)Math.Round(Math.Max(0, Math.Min(255, r)));
                byte gByte = (byte)Math.Round(Math.Max(0, Math.Min(255, g)));
                byte bByte = (byte)Math.Round(Math.Max(0, Math.Min(255, b)));

                LogMessage($" Setting RGB: {rByte}, {gByte}, {bByte}", ConsoleColor.Cyan);
                
                lock (statesLock)
                {
                    foreach (var state in dotStates.Values)
                    {
                        state.RGB[0] = rByte;
                        state.RGB[1] = gByte;
                        state.RGB[2] = bByte;
                        state.UpdatePending = true;
                    }
                }
            }
            else if (message.Address == "/datafeel/vibration/intensity" && message.Count >= 1)
            {
                if (!float.TryParse(message[0].ToString(), out float intensity))
                {
                    LogMessage("Invalid intensity value received", ConsoleColor.Red);
                    return;
                }

                // Clamp intensity between 0 and 1
                intensity = Math.Max(0, Math.Min(1, intensity));
                LogMessage($" Setting Intensity: {intensity}", ConsoleColor.Cyan);
                
                lock (statesLock)
                {
                    foreach (var state in dotStates.Values)
                    {
                        state.Vibration = intensity;
                        state.UpdatePending = true;
                    }
                }
            }
            else if (message.Address == "/datafeel/vibration/frequency" && message.Count >= 1)
            {
                if (!float.TryParse(message[0].ToString(), out float frequency))
                {
                    LogMessage("Invalid frequency value received", ConsoleColor.Red);
                    return;
                }

                // Clamp frequency between 0 and 1
                frequency = Math.Max(0, Math.Min(1, frequency));
                LogMessage($" Setting Frequency: {frequency}", ConsoleColor.Cyan);
                
                lock (statesLock)
                {
                    foreach (var state in dotStates.Values)
                    {
                        state.Frequency = frequency;
                        state.UpdatePending = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Error processing OSC message: {ex.Message}", ConsoleColor.Red);
        }
    }

    private static async Task StartDeviceUpdateLoop()
    {
        while (!updateLoopCts?.Token.IsCancellationRequested ?? true)
        {
            try
            {
                var updateTasks = new List<Task>();
                var currentTime = DateTime.Now;

                // Process all pending device updates
                lock (statesLock)
                {
                    foreach (var kvp in dotStates)
                    {
                        var deviceId = kvp.Key;
                        var state = kvp.Value;

                        // Skip if no update is pending or device was recently updated
                        if (!state.UpdatePending || (currentTime - state.LastUpdate) < frameInterval)
                            continue;

                        var dot = manager?.Dots.FirstOrDefault(d => d.Address == deviceId);
                        if (dot == null) continue;

                        // Create update task for this device
                        updateTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                using var cts = new CancellationTokenSource(deviceTimeout);

                                // Set LED state
                                dot.LedMode = LedModes.GlobalManual;
                                dot.GlobalLed.Red = state.RGB[0];
                                dot.GlobalLed.Green = state.RGB[1];
                                dot.GlobalLed.Blue = state.RGB[2];

                                // Set vibration state
                                dot.VibrationMode = VibrationModes.Manual;
                                dot.VibrationIntensity = state.Vibration * 100; // Scale to 0-100
                                dot.VibrationFrequency = state.Frequency * 100; // Scale to 0-100

                                // Write changes with fire-and-forget for better performance
                                await dot.Write(true, cts.Token);

                                // Mark update as processed
                                lock (statesLock)
                                {
                                    state.UpdatePending = false;
                                    state.LastUpdate = DateTime.Now;
                                }
                            }
                            catch (Exception ex)
                            {
                                LogMessage($"Error updating device {deviceId}: {ex.Message}", ConsoleColor.Red);
                            }
                        }));
                    }
                }

                // Wait for all device updates to complete
                if (updateTasks.Count > 0)
                {
                    await Task.WhenAll(updateTasks);
                }

                // Small delay to prevent CPU thrashing
                await Task.Delay(1);
            }
            catch (Exception ex)
            {
                LogMessage($"Error in update loop: {ex.Message}", ConsoleColor.Red);
                await Task.Delay(100); // Longer delay on error
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
        lock (consoleLock)
        {
            var currentPos = Console.CursorTop;
            Console.SetCursorPosition(0, Console.WindowHeight - 2);
            Console.Write(new string(' ', Console.WindowWidth)); // Clear the line
            Console.SetCursorPosition(0, Console.WindowHeight - 2);
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write("Devices:");
            foreach (var state in dotStates.Values)
            {
                Console.Write($" RGB: ({state.RGB[0]}, {state.RGB[1]}, {state.RGB[2]}) | Vibration: {state.Vibration:F1}%");
            }
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
