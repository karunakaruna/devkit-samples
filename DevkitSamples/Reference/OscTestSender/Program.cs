using Rug.Osc;
using System.Text.Json;

class Program
{
    static async Task Main(string[] args)
    {
        using var sender = new OscSender("127.0.0.1", 0, 9001);
        sender.Connect();

        Console.WriteLine("OSC Test Sender started. Press Ctrl+C to stop.");
        Console.CancelKeyPress += (s, e) => {
            e.Cancel = true;
            Environment.Exit(0);
        };

        var testData = new
        {
            Devices = new Dictionary<string, object>
            {
                ["1"] = new { RGB = new[] { 255, 0, 0 }, Vibration = 0.5f, Frequency = 150f },
                ["2"] = new { RGB = new[] { 0, 255, 0 }, Vibration = 0.7f, Frequency = 160f }
            }
        };

        while (true)
        {
            try
            {
                var json = JsonSerializer.Serialize(testData);
                sender.Send(new OscMessage("/datafeel/batch_update", json));
                Console.WriteLine("Sent test OSC message");
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending OSC message: {ex.Message}");
                await Task.Delay(1000);
            }
        }
    }
}
