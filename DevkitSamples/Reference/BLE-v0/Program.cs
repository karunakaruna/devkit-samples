using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Starting BLE device scan for 10 seconds...");
        var devices = new Dictionary<ulong, (string Name, int Rssi)>();
        var scanCompleted = new ManualResetEvent(false);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (sender, args) =>
        {
            try
            {
                if (!devices.ContainsKey(args.BluetoothAddress))
                {
                    var name = args.Advertisement.LocalName;
                    if (string.IsNullOrEmpty(name))
                    {
                        name = $"Unknown Device ({args.BluetoothAddress:X})";
                    }
                    devices[args.BluetoothAddress] = (name, args.RawSignalStrengthInDBm);
                    Console.WriteLine($"Found device {devices.Count}: {name} (RSSI: {args.RawSignalStrengthInDBm} dBm)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing device: {ex.Message}");
            }
        };

        watcher.Start();
        await Task.Delay(10000); // Scan for 10 seconds
        watcher.Stop();

        if (devices.Count == 0)
        {
            Console.WriteLine("No devices found. Please try again.");
            return;
        }

        Console.WriteLine("\nScanning complete. Found devices:");
        var deviceList = new List<(ulong Address, string Name, int Rssi)>();
        int index = 1;
        foreach (var device in devices)
        {
            Console.WriteLine($"{index}. {device.Value.Name} (RSSI: {device.Value.Rssi} dBm)");
            deviceList.Add((device.Key, device.Value.Name, device.Value.Rssi));
            index++;
        }

        Console.Write("\nEnter the number of the device you want to inspect (1-{0}): ", devices.Count);
        if (int.TryParse(Console.ReadLine(), out int selection) && selection >= 1 && selection <= devices.Count)
        {
            var selectedDevice = deviceList[selection - 1];
            Console.WriteLine($"\nConnecting to {selectedDevice.Name}...");

            try
            {
                var bluetoothDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(selectedDevice.Address);
                if (bluetoothDevice != null)
                {
                    Console.WriteLine("\nDevice Details:");
                    Console.WriteLine($"Name: {bluetoothDevice.Name}");
                    Console.WriteLine($"Address: {selectedDevice.Address:X}");
                    Console.WriteLine($"Connection Status: {bluetoothDevice.ConnectionStatus}");

                    var services = await bluetoothDevice.GetGattServicesAsync();
                    if (services.Status == GattCommunicationStatus.Success)
                    {
                        Console.WriteLine("\nServices:");
                        foreach (var service in services.Services)
                        {
                            Console.WriteLine($"\nService: {service.Uuid}");
                            
                            var characteristics = await service.GetCharacteristicsAsync();
                            if (characteristics.Status == GattCommunicationStatus.Success)
                            {
                                foreach (var characteristic in characteristics.Characteristics)
                                {
                                    Console.WriteLine($"  Characteristic: {characteristic.Uuid}");
                                    Console.WriteLine($"    Properties: {characteristic.CharacteristicProperties}");
                                }
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("Failed to get GATT services.");
                    }
                }
                else
                {
                    Console.WriteLine("Failed to connect to device.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error while connecting to device: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("Invalid selection.");
        }
    }
}
