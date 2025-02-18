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
    private static readonly CancellationTokenSource programCts = new();
    private static OscReceiver? oscReceiver;
    private const int OSC_PORT = 9001;

    static async Task Main(string[] args)
    {
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
                                        var dot = dots[deviceId];
                                        dot.GlobalLed.Red = (byte)device.Value.RGB[0];
                                        dot.GlobalLed.Green = (byte)device.Value.RGB[1];
                                        dot.GlobalLed.Blue = (byte)device.Value.RGB[2];
                                        dot.VibrationIntensity = device.Value.Vibration;
                                        dot.VibrationFrequency = device.Value.Frequency;

                                        Console.WriteLine($"🔵 OSC Update: Device {deviceId} - RGB: {dot.GlobalLed.Red},{dot.GlobalLed.Green},{dot.GlobalLed.Blue}");
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
                Console.WriteLine($"OSC processing error: {e.Message}");
                await Task.Delay(100);
            }
        }
    }

    private static async Task UpdateDevices()
    {
        while (!programCts.Token.IsCancellationRequested)
        {
            foreach (var dot in dots.Values)
            {
                await Task.Run(async () =>
                {
                    bool success = false;
                    int attempt = 0;
                    int maxAttempts = 3; // Retry up to 3 times with increasing timeouts

                    while (!success && attempt < maxAttempts)
                    {
                        try
                        {
                            int timeout = 250 * (attempt + 1); // Exponential backoff
                            using var writeCts = new CancellationTokenSource(timeout);

                            await manager.Write(dot, true, writeCts.Token);
                            Console.WriteLine($"✔ Device {dot.Address} updated successfully.");
                            
                            success = true; // Mark as success if we get here
                        }
                        catch (OperationCanceledException)
                        {
                            Console.WriteLine($"⚠ Device {dot.Address} write timeout (Attempt {attempt + 1}). Retrying...");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"❌ Device {dot.Address} failed: {e.Message}");
                            break; // Exit retry loop on non-timeout errors
                        }

                        attempt++;
                    }

                    // Small delay between device writes to prevent bus congestion
                    await Task.Delay(10);
                });
            }

            // Wait a bit before next batch update
            await Task.Delay(20);
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
}
