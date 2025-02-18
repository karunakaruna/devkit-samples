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
    private static TimeSpan frameInterval = TimeSpan.FromSeconds(1.0 / 30.0); // 30 FPS
    private static readonly TimeSpan deviceTimeout = TimeSpan.FromMilliseconds(100);
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
    private static readonly Dictionary<int, DateTime> lastUpdateAttempts = new();
    private static readonly TimeSpan minUpdateInterval = TimeSpan.FromMilliseconds(33); // ~30fps max per device
    private static readonly Dictionary<int, int> updateCount = new();

    private class DotState
    {
        public byte[] RGB { get; set; } = new byte[3];
        public float VibrationIntensity { get; set; }
        public float VibrationFrequency { get; set; } = 150f;
        public bool UpdatePending { get; set; }
        public DotPropsWritable Props { get; set; }
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
        try
        {
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                isRunning = false;
            };

            var ports = System.IO.Ports.SerialPort.GetPortNames();
            if (ports.Length == 0)
            {
                Console.WriteLine("No COM ports found!");
                return;
            }

            bool connected = false;
            foreach (var port in ports)
            {
                try
                {
                    var serialClient = new DatafeelModbusClientConfiguration()
                        .UseWindowsSerialPortTransceiver()
                        .CreateClient();
                    await RetryOperation(async () =>
                    {
                        await serialClient.Open();
                    }, maxRetries: 3);

                    manager = new DotManagerConfiguration()
                        .AddDot<Dot_63x_xxx>(1)
                        .AddDot<Dot_63x_xxx>(2)
                        .AddDot<Dot_63x_xxx>(3)
                        .AddDot<Dot_63x_xxx>(4)
                        .CreateDotManager();

                    bool result = false;
                    using var cts = new CancellationTokenSource(CONNECTION_TIMEOUT);
                    result = await manager.Start(new List<DatafeelModbusClient> { serialClient }, cts.Token);
                    if (result)
                    {
                        connected = true;
                        foreach (var dot in manager.Dots)
                        {
                            dotStates[dot.Address] = new DotState();
                        }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to connect on {port}: {ex.Message}");
                    continue;
                }
            }

            if (!connected)
            {
                Console.WriteLine("Failed to connect to any Datafeel devices.");
                return;
            }

            InitializeOsc();

            updateLoopCts = new CancellationTokenSource();
            updateLoop = StartDeviceUpdateLoop();

            _ = Task.Run(ProcessMessages);

            while (isRunning)
            {
                await Task.Delay(100);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error: {ex.Message}");
        }
        finally
        {
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
            }
        }
    }

    private static async Task ProcessMessages()
    {
        while (isRunning && oscReceiver != null)
        {
            try
            {
                var receiveTask = Task.Run(() =>
                {
                    try
                    {
                        if (oscReceiver.State != OscSocketState.Connected)
                            return null;

                        return oscReceiver.Receive();
                    }
                    catch (SocketException)
                    {
                        return null;
                    }
                });

                if (await Task.WhenAny(receiveTask, Task.Delay(frameInterval)) == receiveTask)
                {
                    var packet = await receiveTask;
                    if (packet != null)
                    {
                        if (messageQueue.Count < MAX_QUEUE_SIZE)
                        {
                            messageQueue.Enqueue(packet);
                        }
                        else
                        {
                            Console.WriteLine("Message queue overflow, discarding packet");
                        }
                        await ProcessMessageQueue();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing messages: {ex.Message}");
            }
        }
    }

    private static async Task ProcessMessageQueue()
    {
        if (!await processingThrottle.WaitAsync(1))
            return;

        try
        {
            var now = DateTime.UtcNow;
            if ((now - lastMessageProcessed) < messageProcessInterval)
            {
                return;
            }

            lastMessageProcessed = now;

            while (messageQueue.Count > MAX_QUEUE_SIZE)
            {
                messageQueue.TryDequeue(out _);
            }

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
            Console.WriteLine($"Processing OSC message: {message.Address}");
            
            if (message.Address == "/datafeel/batch_update" && message.Count >= 1)
            {
                var jsonData = message[0].ToString();
                if (jsonData == null) return;
                await ProcessBatchUpdate(jsonData);
            }
            else if (message.Address == "/datafeel/led/rgb" && message.Count >= 3)
            {
                if (!float.TryParse(message[0].ToString(), out float r) ||
                    !float.TryParse(message[1].ToString(), out float g) ||
                    !float.TryParse(message[2].ToString(), out float b))
                {
                    Console.WriteLine($"Failed to parse RGB values from: {message[0]}, {message[1]}, {message[2]}");
                    return;
                }

                byte rByte = (byte)Math.Round(Math.Max(0, Math.Min(255, r)));
                byte gByte = (byte)Math.Round(Math.Max(0, Math.Min(255, g)));
                byte bByte = (byte)Math.Round(Math.Max(0, Math.Min(255, b)));

                Console.WriteLine($"Setting RGB values: ({rByte},{gByte},{bByte})");

                lock (statesLock)
                {
                    foreach (var state in dotStates.Values.Where(s => s.Props != null))
                    {
                        state.RGB[0] = rByte;
                        state.RGB[1] = gByte;
                        state.RGB[2] = bByte;
                        state.UpdatePending = true;
                        Console.WriteLine($"Queued RGB update for Device {state.Props.Address}");
                    }
                }
            }
            else if (message.Address == "/datafeel/vibration/intensity" || 
                     message.Address == "/datafeel/vibration/frequency")
            {
                if (message.Count < 1 || !float.TryParse(message[0].ToString(), out float value))
                {
                    Console.WriteLine($"Failed to parse value from: {message[0]}");
                    return;
                }

                Console.WriteLine($"Setting {message.Address} value: {value}");

                lock (statesLock)
                {
                    foreach (var state in dotStates.Values.Where(s => s.Props != null))
                    {
                        if (message.Address == "/datafeel/vibration/intensity")
                        {
                            state.VibrationIntensity = Math.Max(0, Math.Min(1, value));
                            Console.WriteLine($"Queued vibration intensity update for Device {state.Props.Address}: {state.VibrationIntensity}");
                        }
                        else
                        {
                            state.VibrationFrequency = Math.Max(0, Math.Min(255, value));
                            Console.WriteLine($"Queued vibration frequency update for Device {state.Props.Address}: {state.VibrationFrequency}");
                        }
                        state.UpdatePending = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing message {message.Address}: {ex.Message}");
        }
    }

    private static void LogDeviceStatus()
    {
        lock (statesLock)
        {
            Console.WriteLine("\n=== Device Status ===");
            foreach (var state in dotStates)
            {
                var lastUpdateText = lastUpdateAttempts.TryGetValue(state.Key, out var time) 
                    ? $"{(DateTime.UtcNow - time).TotalMilliseconds:F1}ms ago" 
                    : "never";
                    
                Console.WriteLine(
                    $"Device {state.Key}: " +
                    $"UpdatePending={state.Value.UpdatePending}, " +
                    $"RGB=({state.Value.RGB[0]},{state.Value.RGB[1]},{state.Value.RGB[2]}), " +
                    $"LastUpdate={lastUpdateText}");
            }
            Console.WriteLine("==================\n");
        }
    }

    private static async Task StartDeviceUpdateLoop()
    {
        if (manager == null) return;

        await InitializeDots();
        Console.WriteLine("Device update loop started");
        
        while (isRunning)
        {
            try
            {
                var currentTime = DateTime.UtcNow;
                var devicesToUpdate = new List<DotState>();
                
                lock (statesLock)
                {
                    foreach (var state in dotStates.Values.Where(s => s.Props != null))
                    {
                        if (!state.UpdatePending) 
                        {
                            Console.WriteLine($"Device {state.Props.Address} skipped - no update pending");
                            continue;
                        }
                        
                        if (!lastUpdateAttempts.TryGetValue(state.Props.Address, out var lastAttempt) ||
                            (currentTime - lastAttempt) >= minUpdateInterval)
                        {
                            devicesToUpdate.Add(state);
                            Console.WriteLine($"Device {state.Props.Address} queued for update");
                        }
                        else
                        {
                            var timeSinceLastUpdate = (currentTime - lastAttempt).TotalMilliseconds;
                            Console.WriteLine($"Device {state.Props.Address} skipped - too soon since last update ({timeSinceLastUpdate:F1}ms)");
                        }
                    }
                }

                foreach (var state in devicesToUpdate)
                {
                    try
                    {
                        if (state.Props.GlobalLed != null)
                        {
                            state.Props.GlobalLed.Red = state.RGB[0];
                            state.Props.GlobalLed.Green = state.RGB[1];
                            state.Props.GlobalLed.Blue = state.RGB[2];
                        }
                        
                        state.Props.VibrationIntensity = (byte)(state.VibrationIntensity * 255);
                        state.Props.VibrationFrequency = (byte)state.VibrationFrequency;

                        var deviceId = state.Props.Address;
                        lastUpdateAttempts[deviceId] = currentTime;

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var cts = new CancellationTokenSource(deviceTimeout);
                                await manager.Write(state.Props, fireAndForget: true, cts.Token);
                                
                                lock (statesLock)
                                {
                                    if (!updateCount.ContainsKey(deviceId))
                                        updateCount[deviceId] = 0;
                                    updateCount[deviceId]++;
                                    
                                    state.UpdatePending = false;
                                }
                                
                                Console.WriteLine($"Successfully updated Device {deviceId} (Total updates: {updateCount[deviceId]})");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Failed to update Device {deviceId}: {ex.Message}");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error preparing update for Device {state.Props.Address}: {ex.Message}");
                    }
                }

                if (currentTime.Second % 5 == 0) // Log status every 5 seconds
                {
                    LogDeviceStatus();
                }

                await Task.Delay(frameInterval);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in update loop: {ex.Message}");
                await Task.Delay(frameInterval);
            }
        }
    }

    private static async Task InitializeDots()
    {
        Console.WriteLine("Initializing dots...");
        foreach (var dot in manager.Dots)
        {
            Console.WriteLine($"Initializing Device {dot.Address}");
            var props = new DotPropsWritable 
            { 
                Address = dot.Address,
                GlobalLed = new(),
                LedMode = LedModes.GlobalManual,
                VibrationMode = VibrationModes.Manual,
                VibrationIntensity = 0,
                VibrationFrequency = 150
            };
            
            dotStates[dot.Address] = new DotState { Props = props };
            Console.WriteLine($"Device {dot.Address} initialized with props: " +
                $"LED Mode={props.LedMode}, Vib Mode={props.VibrationMode}");
        }
        Console.WriteLine($"Initialized {dotStates.Count} devices");
    }

    private static async Task ProcessBatchUpdate(string jsonData)
    {
        try
        {
            var batchUpdate = JsonDocument.Parse(jsonData);
            var devices = batchUpdate.RootElement.GetProperty("devices");

            lock (statesLock)
            {
                foreach (var device in devices.EnumerateObject())
                {
                    if (int.TryParse(device.Name, out int address))
                    {
                        var deviceData = device.Value;
                        if (!dotStates.TryGetValue(address, out var state))
                        {
                            state = new DotState();
                            dotStates[address] = state;
                        }

                        // Handle RGB values (already in 0-255 range from TouchDesigner)
                        var rgb = deviceData.GetProperty("rgb").EnumerateArray().ToArray();
                        state.RGB[0] = (byte)rgb[0].GetInt32();
                        state.RGB[1] = (byte)rgb[1].GetInt32();
                        state.RGB[2] = (byte)rgb[2].GetInt32();

                        // Handle vibration (0-1 range from TouchDesigner)
                        state.VibrationIntensity = (float)deviceData.GetProperty("vibration").GetDouble();

                        // Handle frequency (raw frequency value from TouchDesigner)
                        state.VibrationFrequency = (float)deviceData.GetProperty("frequency").GetDouble();

                        state.UpdatePending = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing batch update: {ex.Message}");
        }
    }

    private static async Task RetryOperation(Func<Task> operation, int maxRetries)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await operation();
                return;
            }
            catch (Exception ex)
            {
                if (i == maxRetries - 1) throw; // Last attempt
                Console.WriteLine($"Retry {i + 1}/{maxRetries}: {ex.Message}");
                await Task.Delay(50 * (i + 1)); // Exponential backoff
            }
        }
    }

    private static void InitializeOsc()
    {
        try
        {
            var port = OSC_PORT;
            oscReceiver = new OscReceiver(port);
            oscReceiver.Connect();
        }
        catch (Exception ex)
        {
        }
    }

    private static async Task HandleVibrationMessage(OscMessage message)
    {
        if (message.Count != 1)
        {
            Console.WriteLine("Invalid vibration message format");
            return;
        }

        var intensity = Convert.ToSingle(message[0]);
        intensity = Math.Max(0, Math.Min(1, intensity)); // Clamp between 0 and 1

        lock (statesLock)
        {
            foreach (var state in dotStates.Values)
            {
                state.VibrationIntensity = intensity;
                state.UpdatePending = true;
            }
        }

        Console.WriteLine($"Set vibration intensity to {intensity}");
    }

    private static async Task HandleFrequencyMessage(OscMessage message)
    {
        if (message.Count != 1)
        {
            Console.WriteLine("Invalid frequency message format");
            return;
        }

        var frequency = Convert.ToSingle(message[0]);
        frequency = Math.Max(0, Math.Min(1, frequency)); // Clamp between 0 and 1

        lock (statesLock)
        {
            foreach (var state in dotStates.Values)
            {
                state.VibrationFrequency = frequency;
                state.UpdatePending = true;
            }
        }

        Console.WriteLine($"Set vibration frequency to {frequency}");
    }
}
