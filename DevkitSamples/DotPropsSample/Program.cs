using Datafeel;
using Datafeel.NET.Serial;
using Datafeel.NET.BLE;
using Rug.Osc;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

class OptimizedOSCReceiver
{
    private static DotManager? manager;
    private static readonly ConcurrentDictionary<int, DotPropsWritable> dots = new();
    private static readonly ConcurrentDictionary<string, (byte Red, byte Green, byte Blue)> lastKnownStates = new();
    private static readonly ConcurrentDictionary<string, DateTime> lastUpdateTimes = new();
    private static readonly ConcurrentDictionary<int, Queue<(byte R, byte G, byte B)>> oscUpdateQueues = new();
    private static readonly ConcurrentDictionary<int, DateTime> lastOscUpdateTimes = new();
    private static readonly ConcurrentDictionary<int, (int UpdateCount, int ErrorCount, bool IsConnected)> deviceStats = new();
    private static readonly Stopwatch uptime = new();
    private static DateTime lastConsoleUpdate = DateTime.UtcNow;
    private static DateTime lastOscMessage = DateTime.MinValue;
    private static int totalOscMessages = 0;
    private const int ConsoleUpdateIntervalMs = 100;
    private static readonly CancellationTokenSource programCts = new();
    private static OscReceiver? oscReceiver;
    private const int OSC_PORT = 9001;
    private const int MinUpdateIntervalMs = 33; // ~30fps max update rate
    private const int ColorChangeThreshold = 5; // Only update if color changes by more than this
    private const int OscUpdateIntervalMs = 200; // Process OSC updates every 200ms
    private const int MaxQueueSize = 20; // Keep last 20 updates for smoothing

    static async Task Main(string[] args)
    {
        uptime.Start();
        Console.CancelKeyPress += (s, e) => {
            e.Cancel = true;
            programCts.Cancel();
        };

        // Initialize DataFeel Manager
        manager = new DotManagerConfiguration()
            .AddDot<Dot_63x_xxx>(1)
            .AddDot<Dot_63x_xxx>(2)
            .AddDot<Dot_63x_xxx>(3)
            .AddDot<Dot_63x_xxx>(4)
            .CreateDotManager();

        for (int i = 1; i <= 4; i++)
        {
            dots[i] = new DotPropsWritable
            {
                Address = (byte)i,
                LedMode = LedModes.GlobalManual,
                GlobalLed = new(),
                VibrationMode = VibrationModes.Manual,
                VibrationIntensity = 0,
                VibrationFrequency = 178.2f
            };
        }

        // Start Modbus Communication
        try
        {
            var bleClient = new DatafeelModbusClientConfiguration()
                .UseNetBleTransceiver()
                .CreateClient();

            Console.WriteLine("Initializing BLE client and scanning for devices...");
            try 
            {
                using var startCts = new CancellationTokenSource(10000);
                var result = await manager.Start(new List<DatafeelModbusClient> { bleClient }, startCts.Token);
                if (result)
                {
                    Console.WriteLine("BLE client started successfully");
                    if (manager.Dots != null)
                    {
                        foreach (var dot in manager.Dots)
                        {
                            if (dot != null)
                            {
                                Console.WriteLine($"Found DOT device: Address={dot.Address}");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("No DOT devices found in manager.Dots");
                    }
                }
                else
                {
                    Console.WriteLine("Failed to start BLE client");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BLE Error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return;
            }

            Console.WriteLine("✅ Connected to DataFeel devices.");

            // Run a Pre-Test Check
            var preTestSuccess = await RunDevicePreTest();
            if (!preTestSuccess)
            {
                Console.WriteLine("⚠ Pre-test failed. Check device connections.");
                return;
            }

            // Initialize OSC Receiver
            oscReceiver = new OscReceiver(OSC_PORT);
            oscReceiver.Connect();
            Console.WriteLine($"🎵 Listening for OSC on port {OSC_PORT}");

            // Start processing OSC messages
            _ = Task.Run(ProcessOscMessages);

            // Start update loop
            await UpdateDevices();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error: {e.Message}");
        }
        finally
        {
            oscReceiver?.Close();
        }
    }

    private static async Task<bool> RunDevicePreTest()
    {
        Console.WriteLine("🔍 Running Device Pre-Test...");

        foreach (var dot in dots.Values)
        {
            try
            {
                using var readCts = new CancellationTokenSource(500);

                // Read device information
                var result = await manager.Read(dot, readCts.Token);
                Console.WriteLine($"📡 Device {dot.Address} - Serial: {result.SerialNumber}, Temp: {result.SkinTemperature}°C");

                // Flash LEDs as a test signal
                dot.GlobalLed.Red = 255;
                dot.GlobalLed.Green = 255;
                dot.GlobalLed.Blue = 255;

                using var writeCts = new CancellationTokenSource(500);
                await manager.Write(dot, true, writeCts.Token);

                await Task.Delay(200);

                // Turn LEDs back off
                dot.GlobalLed.Red = 0;
                dot.GlobalLed.Green = 0;
                dot.GlobalLed.Blue = 0;
                await manager.Write(dot, true, writeCts.Token);
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ Device {dot.Address} failed to respond: {e.Message}");
                return false;
            }
        }

        Console.WriteLine("✅ All devices passed pre-test.");
        return true;
    }

    private static async Task ProcessOscMessages()
    {
        if (oscReceiver == null) return;

        while (!programCts.Token.IsCancellationRequested)
        {
            try
            {
                if (oscReceiver.State != OscSocketState.Connected)
                {
                    await Task.Delay(100);
                    continue;
                }

                while (oscReceiver.TryReceive(out OscPacket? packet))
                {
                    lastOscMessage = DateTime.UtcNow;
                    totalOscMessages++;
                    
                    if (packet is OscMessage msg && msg.Address == "/datafeel/batch_update" && msg.Count > 0 && msg[0] is string json)
                    {
                        try
                        {
                            var update = JsonSerializer.Deserialize<BatchUpdate>(json);
                            if (update != null)
                            {
                                foreach (var device in update.Devices)
                                {
                                    if (int.TryParse(device.Key, out int deviceId) && dots.ContainsKey(deviceId))
                                    {
                                        if (!oscUpdateQueues.ContainsKey(deviceId))
                                        {
                                            oscUpdateQueues[deviceId] = new Queue<(byte R, byte G, byte B)>();
                                        }

                                        var queue = oscUpdateQueues[deviceId];
                                        
                                        queue.Enqueue(((byte)device.Value.RGB[0], 
                                                     (byte)device.Value.RGB[1], 
                                                     (byte)device.Value.RGB[2]));
                                        
                                        while (queue.Count > MaxQueueSize)
                                        {
                                            queue.Dequeue();
                                        }

                                        if (!lastOscUpdateTimes.TryGetValue(deviceId, out var lastUpdate) ||
                                            (DateTime.UtcNow - lastUpdate).TotalMilliseconds >= OscUpdateIntervalMs)
                                        {
                                            var smoothedColors = SmoothColors(queue);
                                            var dot = dots[deviceId];
                                            
                                            dot.GlobalLed.Red = smoothedColors.R;
                                            dot.GlobalLed.Green = smoothedColors.G;
                                            dot.GlobalLed.Blue = smoothedColors.B;
                                            dot.VibrationIntensity = device.Value.Vibration;
                                            dot.VibrationFrequency = device.Value.Frequency;

                                            try 
                                            {
                                                await manager?.Write(dot, true);
                                                UpdateDeviceStat(deviceId, isConnected: true, isUpdate: true);
                                            }
                                            catch (Exception)
                                            {
                                                UpdateDeviceStat(deviceId, isConnected: false, isError: true);
                                            }

                                            lastOscUpdateTimes[deviceId] = DateTime.UtcNow;
                                        }
                                    }
                                }
                            }
                        }
                        catch (JsonException) { }
                    }
                    UpdateConsole();
                }

                await Task.Delay(10);
            }
            catch (Exception e)
            {
                Console.WriteLine($"OSC Error: {e.Message}");
                await Task.Delay(100);
            }
        }
    }

    private static (byte R, byte G, byte B) SmoothColors(Queue<(byte R, byte G, byte B)> updates)
    {
        if (updates.Count == 0) return (0, 0, 0);
        
        // Use exponential moving average for smoother transitions
        double alpha = 0.3; // Smoothing factor
        var first = updates.Peek();
        double r = first.R, g = first.G, b = first.B;
        
        foreach (var update in updates.Skip(1))
        {
            r = alpha * update.R + (1 - alpha) * r;
            g = alpha * update.G + (1 - alpha) * g;
            b = alpha * update.B + (1 - alpha) * b;
        }
        
        return ((byte)r, (byte)g, (byte)b);
    }

    private static byte RoundToBucket(byte value)
    {
        // Round to nearest multiple of threshold to reduce noise
        return (byte)(Math.Round(value / (double)ColorChangeThreshold) * ColorChangeThreshold);
    }

    private static bool HasStateChanged(string address, DotPropsWritable dot)
    {
        // Round the incoming values to reduce noise
        byte newRed = RoundToBucket(dot.GlobalLed.Red);
        byte newGreen = RoundToBucket(dot.GlobalLed.Green);
        byte newBlue = RoundToBucket(dot.GlobalLed.Blue);

        if (!lastKnownStates.TryGetValue(address, out var lastState))
        {
            lastKnownStates[address] = (newRed, newGreen, newBlue);
            return true;
        }

        // Check if any color component has changed by more than the threshold
        bool changed = Math.Abs(lastState.Red - newRed) >= ColorChangeThreshold ||
                      Math.Abs(lastState.Green - newGreen) >= ColorChangeThreshold ||
                      Math.Abs(lastState.Blue - newBlue) >= ColorChangeThreshold;

        if (changed)
        {
            lastKnownStates[address] = (newRed, newGreen, newBlue);
            // Update the actual LED values to match our rounded values
            dot.GlobalLed.Red = newRed;
            dot.GlobalLed.Green = newGreen;
            dot.GlobalLed.Blue = newBlue;
        }

        return changed;
    }

    private static bool ShouldUpdateDevice(string address)
    {
        var now = DateTime.UtcNow;
        if (!lastUpdateTimes.TryGetValue(address, out var lastUpdate))
        {
            lastUpdateTimes[address] = now;
            return true;
        }

        if ((now - lastUpdate).TotalMilliseconds < MinUpdateIntervalMs)
        {
            return false;
        }

        lastUpdateTimes[address] = now;
        return true;
    }

    private static async Task UpdateDevices()
    {
        while (!programCts.Token.IsCancellationRequested)
        {
            var tasks = new List<Task>();

            foreach (var dot in dots.Values)
            {
                // Skip if we're updating too frequently
                if (!ShouldUpdateDevice(dot.Address.ToString()))
                {
                    continue;
                }

                tasks.Add(Task.Run(async () =>
                {
                    // Skip if state hasn't changed
                    if (!HasStateChanged(dot.Address.ToString(), dot))
                    {
                        return;
                    }

                    bool success = false;
                    int attempt = 0;
                    int maxAttempts = 3;

                    while (!success && attempt < maxAttempts && !programCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            int timeout = 250 * (attempt + 1);
                            using var writeCts = new CancellationTokenSource(timeout);
                            using var readCts = new CancellationTokenSource(timeout);

                            await manager?.Write(dot, true);
                            Console.WriteLine($"✅ Device {dot.Address} LED updated to R:{dot.GlobalLed.Red} G:{dot.GlobalLed.Green} B:{dot.GlobalLed.Blue}");

                            try
                            {
                                await manager?.Read(dot, readCts.Token);
                                Console.WriteLine($"🌡 Device {dot.Address} Skin Temp: {dot.SkinTemperature}°C");
                            }
                            catch
                            {
                                // Ignore read failures
                            }

                            success = true;
                        }
                        catch (OperationCanceledException)
                        {
                            Console.WriteLine($"⚠ Device {dot.Address} timeout (Attempt {attempt + 1}/{maxAttempts})");
                            IncrementDeviceStat(dot.Address, true);
                            UpdateConsole();
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"❌ Device {dot.Address} error: {e.Message}");
                            IncrementDeviceStat(dot.Address, true);
                            UpdateConsole();
                        }

                        attempt++;
                        if (!success && attempt < maxAttempts)
                        {
                            await Task.Delay(50);
                        }
                    }
                }));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                // Ignore any task failures
            }

            await Task.Delay(MinUpdateIntervalMs); // Ensure we don't exceed max frame rate
        }
    }

    private static void UpdateConsole()
    {
        var now = DateTime.UtcNow;
        if ((now - lastConsoleUpdate).TotalMilliseconds < ConsoleUpdateIntervalMs)
        {
            return;
        }
        lastConsoleUpdate = now;

        Console.Clear();
        Console.WriteLine($"DataFeel Performance Monitor - Uptime: {uptime.Elapsed:hh\\:mm\\:ss}");
        Console.WriteLine("═══════════════════════════════════════════════════");
        
        // OSC Status
        var oscStatus = (now - lastOscMessage).TotalMilliseconds < 1000 ? "🟢" : "🔴";
        Console.WriteLine($"OSC Status: {oscStatus} Port: {OSC_PORT} | Messages: {totalOscMessages} | Last Message: {(now - lastOscMessage).TotalMilliseconds:F0}ms ago");
        Console.WriteLine($"Settings: Update Rate: {OscUpdateIntervalMs}ms | Smoothing Window: {MaxQueueSize} samples");
        Console.WriteLine("───────────────────────────────────────────────────");
        Console.WriteLine("Device Status:");

        foreach (var deviceId in dots.Keys.OrderBy(k => k))
        {
            if (!deviceStats.TryGetValue(deviceId, out var stats))
            {
                stats = (0, 0, false);
            }

            var dot = dots[deviceId];
            var lastUpdate = lastOscUpdateTimes.TryGetValue(deviceId, out var time) 
                ? (DateTime.UtcNow - time).TotalMilliseconds 
                : double.MaxValue;

            var status = stats.IsConnected ? "🟢" : "🔴";
            var updateStatus = lastUpdate < 1000 ? "✓" : "⨯";
            
            Console.WriteLine($"{status} Device {deviceId}: RGB({dot.GlobalLed.Red,3},{dot.GlobalLed.Green,3},{dot.GlobalLed.Blue,3}) " +
                          $"| Updates: {stats.UpdateCount,6} {updateStatus} | Errors: {stats.ErrorCount,4} | Last Update: {(lastUpdate == double.MaxValue ? "Never" : $"{lastUpdate:F0}ms")}");
        }

        Console.WriteLine("───────────────────────────────────────────────────");
        Console.WriteLine("Press Ctrl+C to exit");
    }

    private static void UpdateDeviceStat(int deviceId, bool? isConnected = null, bool isError = false, bool isUpdate = false)
    {
        deviceStats.AddOrUpdate(
            deviceId,
            isError ? (0, 1, isConnected ?? false) : (1, 0, isConnected ?? false),
            (_, stats) => 
            {
                var updates = isUpdate ? stats.UpdateCount + 1 : stats.UpdateCount;
                var errors = isError ? stats.ErrorCount + 1 : stats.ErrorCount;
                var connected = isConnected ?? stats.IsConnected;
                return (updates, errors, connected);
            }
        );
    }

    private static void IncrementDeviceStat(int deviceId, bool isError = false)
    {
        deviceStats.AddOrUpdate(
            deviceId,
            isError ? (0, 1, false) : (1, 0, false),
            (_, stats) => isError ? (stats.UpdateCount, stats.ErrorCount + 1, false) : (stats.UpdateCount + 1, stats.ErrorCount, false)
        );
    }

    private class BatchUpdate
    {
        [JsonPropertyName("timestamp")]
        public double Timestamp { get; set; }

        [JsonPropertyName("devices")]
        public Dictionary<string, DeviceData> Devices { get; set; } = new();
    }

    private class DeviceData
    {
        [JsonPropertyName("rgb")]
        public int[] RGB { get; set; } = new int[3];

        [JsonPropertyName("vibration")]
        public float Vibration { get; set; }

        [JsonPropertyName("frequency")]
        public float Frequency { get; set; }
    }
}
