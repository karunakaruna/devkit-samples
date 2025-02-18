using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Rug.Osc;
using Datafeel;
using Datafeel.NET.Serial;
using Datafeel.NET.BLE;

class Program
{
    static async Task Main(string[] args)
    {
        // Initialize DotManager
        var manager = new DotManagerConfiguration()
            .AddDot<Dot_63x_xxx>(1)
            .AddDot<Dot_63x_xxx>(2)
            .AddDot<Dot_63x_xxx>(3)
            .AddDot<Dot_63x_xxx>(4)
            .CreateDotManager();

        using (var cts = new CancellationTokenSource(10000))
        {
            try
            {
                var serialClient = new DatafeelModbusClientConfiguration()
                    .UseWindowsSerialPortTransceiver()
                    .CreateClient();

                var result = await manager.Start(new List<DatafeelModbusClient> { serialClient });
                if (!result)
                {
                    Console.WriteLine("Failed to start");
                    return;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                return;
            }
        }

        // Set up OSC receiver
        var listenPort = 2390;
        using var receiver = new OscReceiver(listenPort);
        receiver.Connect();
        Console.WriteLine($"Listening for OSC messages on port {listenPort}...");

        while (true)
        {
            try
            {
                var packet = await Task.Run(() => receiver.Receive());
                if (packet is OscMessage message && message.Address == "/affect" && message.Count == 2)
                {
                    var affectType = message[0] as string;
                    var intensity = Convert.ToSingle(message[1]);

                    byte red = 0, blue = 0;

                    switch (affectType?.ToLower())
                    {
                        case "negative":
                            red = (byte)(intensity * 255);
                            blue = 0;
                            break;
                        case "positive":
                            blue = (byte)(intensity * 255);
                            red = 0;
                            break;
                        case "neutral":
                            red = blue = (byte)(intensity * 255 / 2);
                            break;
                        default:
                            Console.WriteLine("Invalid affect type");
                            continue;
                    }

                    foreach (var dot in manager.Dots)
                    {
                        dot.LedMode = LedModes.GlobalManual;
                        dot.GlobalLed.Red = red;
                        dot.GlobalLed.Green = 0;
                        dot.GlobalLed.Blue = blue;

                        try
                        {
                            await manager.Write(dot, fireAndForget: true);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error updating dot: {ex.Message}");
                        }
                    }

                    Console.WriteLine($"Affect: {affectType}, Intensity: {intensity}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error receiving OSC message: {ex.Message}");
            }

            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
            {
                break;
            }
        }

        Console.WriteLine("OSC affect handler stopped.");
    }
}