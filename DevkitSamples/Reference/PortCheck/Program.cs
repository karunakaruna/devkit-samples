using Datafeel;
using Datafeel.NET.Serial;
using System;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        var client = new DatafeelModbusClientConfiguration()
            .UseWindowsSerialPortTransceiver()
            .CreateClient();

        try
        {
            await client.Open();
            Console.WriteLine("Device connection opened.");

            var myDot = new Dot_63x_xxx(1);

            using (var readCancelSource = new CancellationTokenSource(2000))
            {
                    bool readSuccess = await myDot.ReadAllSettings(client, readCancelSource.Token);
                if (readSuccess)
                {
                    Console.WriteLine($"Device is responsive!");
                    Console.WriteLine($"Hardware ID: {myDot.HardwareID}");
                    Console.WriteLine($"Firmware Version: {myDot.FirmwareID}");
                    Console.WriteLine($"Skin Temperature: {myDot.SkinTemperature}°C");
                }
                else
                {
                    Console.WriteLine("Device failed to respond.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            await client.Close();
        }
    }
}
