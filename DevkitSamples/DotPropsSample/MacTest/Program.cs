using System;
using System.Runtime.InteropServices;

class Program
{
    static void Main(string[] args)
    {
        try
        {
            // Print basic info without any system calls
            Console.WriteLine("Basic test starting...");
            Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
            Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
            Console.WriteLine("Test complete.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error occurred: {ex.Message}");
        }
    }
}
