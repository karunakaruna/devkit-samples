using Datafeel;
using Datafeel.NET.Serial;
using Datafeel.NET.BLE;
using System.Diagnostics;
using System.Collections.Concurrent;

class OptimizedBLETest
{
    private static DotManager? manager;
    private static readonly ConcurrentDictionary<int, DotPropsWritable> dots = new();
    private static readonly CancellationTokenSource programCts = new();

    static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            programCts.Cancel();
        };

        // Initialize manager with BLE & Serial support
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

        // Start Modbus Communication (BLE + Serial)
        if (!await InitializeDevices())
        {
            Console.WriteLine("❌ Device initialization failed.");
            return;
        }

        Console.WriteLine("✅ All devices ready. Starting update loop...");

        // Start BLE-optimized update loop
        await UpdateDevices();
    }

    private static async Task<bool> InitializeDevices()
    {
        Console.WriteLine("🔌 Initializing devices with Serial + BLE...");

        try
        {
            using var startCts = new CancellationTokenSource(5000);

            var serialClient = new DatafeelModbusClientConfiguration()
                .UseWindowsSerialPortTransceiver()
                .CreateClient();

            var bleClient = new DatafeelModbusClientConfiguration()
                .UseNetBleTransceiver()
                .CreateClient();

            var clients = new List<DatafeelModbusClient> { serialClient, bleClient };
            var result = await manager.Start(clients, startCts.Token);

            if (!result)
            {
                Console.WriteLine("❌ Failed to connect via Serial & BLE.");
                return false;
            }

            Console.WriteLine("✅ Successfully connected to devices via Serial + BLE.");
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"❌ Device initialization error: {e.Message}");
            return false;
        }
    }

    private static async Task UpdateDevices()
    {
        var random = new Random();

        while (!programCts.Token.IsCancellationRequested)
        {
            var tasks = new List<Task>();

            foreach (var dot in dots.Values)
            {
                tasks.Add(Task.Run(async () =>
                {
                    bool success = false;
                    int attempt = 0;
                    int maxAttempts = 3; // Retry up to 3 times

                    while (!success && attempt < maxAttempts)
                    {
                        try
                        {
                            int timeout = 250 * (attempt + 1);
                            using var writeCts = new CancellationTokenSource(timeout);
                            using var readCts = new CancellationTokenSource(timeout);

                            // Set LED to random color
                            dot.GlobalLed.Red = (byte)random.Next(0, 255);
                            dot.GlobalLed.Green = (byte)random.Next(0, 255);
                            dot.GlobalLed.Blue = (byte)random.Next(0, 255);

                            // Write using BLE if available, fallback to Serial
                            if (manager.HasBleSupport)
                            {
                                await manager.Write(dot, useBle: true, writeCts.Token);
                                Console.WriteLine($"✅ (BLE) Device {dot.Address} updated.");
                            }
                            else
                            {
                                await manager.Write(dot, useBle: false, writeCts.Token);
                                Console.WriteLine($"✅ (Serial) Device {dot.Address} updated.");
                            }

                            // Read data (like temperature)
                            await manager.Read(dot, readCts.Token);
                            Console.WriteLine($"🌡 Device {dot.Address} Skin Temp: {dot.SkinTemperature}°C");

                            success = true;
                        }
                        catch (OperationCanceledException)
                        {
                            Console.WriteLine($"⚠ Device {dot.Address} write timeout (Attempt {attempt + 1}). Retrying...");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"❌ Device {dot.Address} failed: {e.Message}");
                            break;
                        }

                        attempt++;
                    }

                    await Task.Delay(10);
                }));
            }

            await Task.WhenAll(tasks);
            await Task.Delay(1000);
        }
    }
}
