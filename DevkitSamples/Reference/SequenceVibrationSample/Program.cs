using Datafeel;
using Datafeel.NET.Serial;
using Datafeel.NET.BLE;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

var manager = new DotManagerConfiguration()
    .AddDot<Dot_63x_xxx>(1)
    .AddDot<Dot_63x_xxx>(2)
    .AddDot<Dot_63x_xxx>(3)
    .AddDot<Dot_63x_xxx>(4)
    .CreateDotManager();

foreach (var d in manager.Dots)
{
    d.VibrationMode = VibrationModes.Library;
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

while (true)
{
    var delay = Task.Delay(2000);
    foreach (var d in manager.Dots)
    {
        d.VibrationMode = VibrationModes.Library;
        //There can be up to 8 waveforms in the sequence
        d.VibrationSequence[0].Waveforms = VibrationWaveforms.StrongBuzzP100;
        d.VibrationSequence[1].Waveforms = VibrationWaveforms.StrongClick1P100;
        d.VibrationSequence[2].Waveforms = VibrationWaveforms.TransitionHum1P100;
        d.VibrationSequence[3].Waveforms = VibrationWaveforms.TransitionRampDownMediumSharp2P50ToP0;
        d.VibrationSequence[4].Waveforms = VibrationWaveforms.TransitionRampUpShortSharp2P0ToP50;
        d.VibrationSequence[5].Waveforms = VibrationWaveforms.DoubleClickP100;
        d.VibrationSequence[6].Waveforms = VibrationWaveforms.TransitionRampUpShortSmooth2P0ToP100;
        d.VibrationSequence[7].Waveforms = VibrationWaveforms.EndSequence;

        // Set this to true, then call Write() to start the sequence
        d.VibrationGo = true;

        try
        {
            await d.Write(fireAndForget: true);
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }
    }
    await delay;
}
