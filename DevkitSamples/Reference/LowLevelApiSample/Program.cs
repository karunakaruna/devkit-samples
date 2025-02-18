using Datafeel;
using Datafeel.NET.Serial;
using Datafeel.NET.BLE;

/*
 * The low level API requires a DatafeelModbusClient to be provided to a HardwareDot object. A specific HardwareDot subclass corresponds to a specific device model.
 */

var commClient = new DatafeelModbusClientConfiguration()
    .UseWindowsSerialPortTransceiver()
    .CreateClient();

var myDot1 = new Dot_63x_xxx(1);
await commClient.Open();

var random = new Random();

while (true)
{
    var delay = Task.Delay(250);

    myDot1.LedMode = LedModes.GlobalManual;
    myDot1.GlobalLed.Red = (byte)random.Next(0, 255);
    myDot1.GlobalLed.Green = (byte)random.Next(0, 255);
    myDot1.GlobalLed.Blue = (byte)random.Next(0, 255);
    try
    {
        using(var writeCancelSource = new CancellationTokenSource(250))
        {
            await myDot1.WriteAllSettings(commClient, writeCancelSource.Token);
        }
        //await myDot1.WriteAllSettings(commClient);
    }
    catch (Exception e)
    {
        Console.WriteLine(e.Message);
    }

    await delay;
}