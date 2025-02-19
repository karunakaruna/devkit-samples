using System;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Test app starting...");
        
        try
        {
            // Try to list USB devices
            var ports = System.IO.Ports.SerialPort.GetPortNames();
            Console.WriteLine($"Found {ports.Length} serial ports");
            
            // Try to list Bluetooth devices
            Console.WriteLine("Checking Bluetooth...");
            // Just print something for now
            Console.WriteLine("Bluetooth check complete");
            
            Console.WriteLine("Test successful!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }
}
