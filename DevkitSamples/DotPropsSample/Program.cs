using Datafeel;
using Datafeel.NET.Serial;
using Datafeel.NET.BLE;
using System.Diagnostics;
using System.Collections.Concurrent;
using Rug.Osc;
using System.Text.Json;
using System.Text.Json.Serialization;

class PerformanceTest
{
    private static DotManager? manager;
    private static readonly List<DotPropsWritable> dots = new();
    private static readonly ConcurrentDictionary<int, (int Updates, Stopwatch Timer, bool HasOscData)> performanceMetrics = new();
    private static readonly ConcurrentDictionary<int, DeviceState> deviceStates = new();
    private static readonly ConcurrentDictionary<int, (int[] RGB, float Vibration, float Frequency)> latestOscData = new();
    private static readonly CancellationTokenSource programCts = new();
    private static OscReceiver? oscReceiver;
    private static readonly ConcurrentQueue<OscPacket> messageQueue = new();
    private const int OSC_PORT = 9001;
    
    private class DeviceState
    {
        public int[] RGB { get; set; } = new int[3];
        public float Vibration { get; set; }
        public float Frequency { get; set; }
        public DateTime LastUpdate { get; set; } = DateTime.MinValue;
    }

    static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (s, e) => {
            e.Cancel = true;
            programCts.Cancel();
        };

        // Initialize devices
        manager = new DotManagerConfiguration()
            .AddDot<Dot_63x_xxx>(1)
            .AddDot<Dot_63x_xxx>(2)
            .AddDot<Dot_63x_xxx>(3)
            .AddDot<Dot_63x_xxx>(4)
            .CreateDotManager();

        // Initialize dot properties
        for (int i = 1; i <= 4; i++)
        {
            dots.Add(new DotPropsWritable() { 
                Address = (byte)i,
                LedMode = LedModes.GlobalManual, 
                GlobalLed = new(),
                VibrationIntensity = 0,
                VibrationFrequency = 150
            });
            performanceMetrics[i] = (0, new Stopwatch(), false);
            deviceStates[i] = new DeviceState();
        }

        // Connect to devices
        try
        {
            using var startCts = new CancellationTokenSource(10000);
            var serialClient = new DatafeelModbusClientConfiguration()
                .UseWindowsSerialPortTransceiver()
                .CreateClient();
            
            if (manager == null)
            {
                Console.WriteLine("Failed to initialize device manager");
                return;
            }

            var result = await manager.Start(new List<DatafeelModbusClient> { serialClient }, startCts.Token);
            if (!result)
            {
                Console.WriteLine("Failed to connect to devices");
                return;
            }
            
            Console.WriteLine("Connected to devices successfully");

            // Initialize OSC receiver
            try
            {
                oscReceiver = new OscReceiver(OSC_PORT);
                oscReceiver.Connect();
                Console.WriteLine($"OSC receiver listening on port {OSC_PORT}");
                
                // Start OSC listening task
                _ = Task.Run(ProcessOscMessages);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to initialize OSC receiver: {e.Message}");
            }
            
            // Start performance monitoring
            var monitorTask = Task.Run(MonitorPerformance);
            
            // Start test patterns
            await RunTestPatterns();
            
            await monitorTask;
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

    private static async Task ProcessOscMessages()
    {
        if (oscReceiver == null) return;

        Console.WriteLine("OSC message processing started");
        while (!programCts.Token.IsCancellationRequested)
        {
            try
            {
                if (oscReceiver.State != OscSocketState.Connected)
                {
                    Console.WriteLine("OSC receiver not connected, attempting to reconnect...");
                    await Task.Delay(100);
                    continue;
                }

                while (oscReceiver.TryReceive(out OscPacket? packet))
                {
                    if (packet is OscMessage msg && msg.Address == "/datafeel/batch_update" && msg.Count > 0 && msg[0] is string json)
                    {
                        try
                        {
                            var update = JsonSerializer.Deserialize<BatchUpdate>(json);
                            if (update != null)
                            {
                                foreach (var device in update.Devices)
                                {
                                    if (int.TryParse(device.Key, out int deviceId))
                                    {
                                        // Update performance metrics
                                        if (performanceMetrics.TryGetValue(deviceId, out var metrics))
                                        {
                                            performanceMetrics[deviceId] = (metrics.Updates, metrics.Timer, true);
                                        }
                                        
                                        // Update device states for display
                                        if (deviceStates.TryGetValue(deviceId, out var state))
                                        {
                                            deviceStates[deviceId] = new DeviceState
                                            {
                                                RGB = device.Value.RGB,
                                                Vibration = device.Value.Vibration,
                                                Frequency = device.Value.Frequency,
                                                LastUpdate = DateTime.Now
                                            };
                                        }

                                        // Store latest data for device write loop
                                        latestOscData.AddOrUpdate(deviceId, 
                                            (device.Value.RGB, device.Value.Vibration, device.Value.Frequency),
                                            (_, _) => (device.Value.RGB, device.Value.Vibration, device.Value.Frequency));
                                    }
                                }
                            }
                        }
                        catch (JsonException) { }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error processing OSC messages: {e.Message}");
                await Task.Delay(100);
            }
        }
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

    private static async Task RunTestPatterns()
    {
        if (manager == null) return;

        Console.WriteLine("Starting performance test...");
        Console.WriteLine("Press Ctrl+C to stop");

        var tasks = new List<Task>();

        foreach (var dot in dots)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (!programCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        // Try to get latest OSC data
                        if (latestOscData.TryGetValue(dot.Address, out var oscData))
                        {
                            // Update RGB only
                            dot.GlobalLed.Red = (byte)oscData.RGB[0];
                            dot.GlobalLed.Green = (byte)oscData.RGB[1];
                            dot.GlobalLed.Blue = (byte)oscData.RGB[2];

                            using var writeCts = new CancellationTokenSource(100);
                            try 
                            {
                                await manager.Write(dot, true, writeCts.Token);
                                // Update the metrics atomically
                                performanceMetrics.AddOrUpdate(dot.Address,
                                    (1, new Stopwatch(), true),
                                    (_, old) => (old.Updates + 1, old.Timer, true));
                            }
                            catch (OperationCanceledException) 
                            {
                                // Ignore cancellation
                            }
                        }
                        
                        // Small delay to prevent overwhelming the device
                        await Task.Delay(1);
                    }
                    catch (Exception e) when (!(e is OperationCanceledException))
                    {
                        Console.WriteLine($"Error updating device {dot.Address}: {e.Message}");
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task MonitorPerformance()
    {
        var lastDisplay = DateTime.Now;
        var lastMetrics = new Dictionary<int, int>();

        while (!programCts.Token.IsCancellationRequested)
        {
            if ((DateTime.Now - lastDisplay).TotalSeconds >= 1)
            {
                Console.Clear();
                Console.WriteLine("\nOSC Data Monitor:");
                Console.WriteLine("----------------");
                foreach (var device in deviceStates.OrderBy(d => d.Key))
                {
                    var status = (DateTime.Now - device.Value.LastUpdate).TotalSeconds < 1 ? "ACTIVE" : "STALE ";
                    Console.WriteLine($"Device {device.Key,2}: [{device.Value.RGB[0],3},{device.Value.RGB[1],3},{device.Value.RGB[2],3}] Vib: {device.Value.Vibration:F2} Freq: {device.Value.Frequency:F1}Hz {status}");
                }

                Console.WriteLine("\nPerformance Metrics:");
                Console.WriteLine("-------------------");
                foreach (var device in performanceMetrics.OrderBy(d => d.Key))
                {
                    var metrics = device.Value;
                    var currentUpdates = metrics.Updates;
                    
                    // Calculate updates since last display
                    var lastCount = lastMetrics.GetValueOrDefault(device.Key, currentUpdates);
                    var updatesDelta = currentUpdates - lastCount;
                    
                    var indicator = metrics.HasOscData ? "○" : "x";
                    Console.WriteLine($"{indicator} Device {device.Key}: {updatesDelta:F2} FPS ({currentUpdates} total updates)");
                    
                    lastMetrics[device.Key] = currentUpdates;
                }

                lastDisplay = DateTime.Now;
            }

            await Task.Delay(100);
        }
    }
}
