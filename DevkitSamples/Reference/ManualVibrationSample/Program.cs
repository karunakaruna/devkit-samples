using Datafeel;
using Datafeel.NET.Serial;
using Datafeel.NET.BLE;

var manager = new DotManagerConfiguration()
    .AddDot<Dot_63x_xxx>(1)
    .AddDot<Dot_63x_xxx>(2)
    .AddDot<Dot_63x_xxx>(3)
    .AddDot<Dot_63x_xxx>(4)
    .CreateDotManager();

var dots = new List<DotPropsWritable>()
{
    new DotPropsWritable() { Address = 1, LedMode = LedModes.Breathe, GlobalLed = new(), VibrationMode = VibrationModes.Manual},
    new DotPropsWritable() { Address = 2, LedMode = LedModes.Breathe, GlobalLed = new(), VibrationMode = VibrationModes.Manual},
    new DotPropsWritable() { Address = 3, LedMode = LedModes.Breathe, GlobalLed = new(), VibrationMode = VibrationModes.Manual},
    new DotPropsWritable() { Address = 4, LedMode = LedModes.Breathe, GlobalLed = new(), VibrationMode = VibrationModes.Manual},
};

foreach(var d in dots)
{
    d.VibrationIntensity = 1.0f;
    d.VibrationFrequency = 150;
}

using (var cts = new CancellationTokenSource(10000))
{
    try
    {
        var serialClient = new DatafeelModbusClientConfiguration()
            .UseWindowsSerialPortTransceiver()
            .CreateClient();
        var bleClient = new DatafeelModbusClientConfiguration()
            .UseNetBleTransceiver()
            .CreateClient();
        var clients = new List<DatafeelModbusClient> { serialClient, bleClient };
        var result = await manager.Start(clients, cts.Token);
        if (result)
        {
            Console.WriteLine("Started");
        }
        else
        {
            Console.WriteLine("Failed to start");
        }
    }
    catch (Exception e)
    {
        Console.WriteLine(e.Message);
    }
}
var random = new Random();

while (true)
{
    var delay = Task.Delay(100);
    foreach (var d in dots)
    {
        d.VibrationIntensity = 1.0f;
        d.VibrationFrequency += 10;
        if(d.VibrationFrequency > 250)
        {
            d.VibrationFrequency = 100;
        }

        try
        {
            using (var writeCancelSource = new CancellationTokenSource(250))
            using (var readCancelSource = new CancellationTokenSource(250))
            {
                await manager.Write(d, false, writeCancelSource.Token);
                var result = await manager.Read(d, readCancelSource.Token);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }
    }
    await delay;
}
