from pythonosc import udp_client

def test_device(client, device_id):
    print(f"\n=== Testing Device {device_id} ===")
    
    # Select device
    print(f"Selecting device {device_id}...")
    client.send_message("/datafeel/device/select", device_id)
    
    # Test LED colors
    print("\nTesting LED colors...")
    
    # Basic colors
    colors = [
        (255, 0, 0),    # Red
        (0, 255, 0),    # Green
        (0, 0, 255),    # Blue
        (255, 255, 0),  # Yellow
        (0, 255, 255),  # Cyan
        (255, 0, 255),  # Magenta
        (255, 255, 255) # White
    ]
    
    # Test basic colors
    for r, g, b in colors:
        print(f"Setting LED to RGB({r}, {g}, {b})")
        client.send_message("/datafeel/led/rgb", [r, g, b])
    
    # Fade through RGB values
    print("\nFading through RGB values...")
    steps = 10  # Number of steps for each fade
    
    # Red fade
    print("Red fade...")
    for i in range(steps + 1):
        val = int((255 * i) / steps)
        print(f"Setting LED to RGB({val}, 0, 0)")
        client.send_message("/datafeel/led/rgb", [val, 0, 0])
    
    # Green fade
    print("Green fade...")
    for i in range(steps + 1):
        val = int((255 * i) / steps)
        print(f"Setting LED to RGB(0, {val}, 0)")
        client.send_message("/datafeel/led/rgb", [0, val, 0])
    
    # Blue fade
    print("Blue fade...")
    for i in range(steps + 1):
        val = int((255 * i) / steps)
        print(f"Setting LED to RGB(0, 0, {val})")
        client.send_message("/datafeel/led/rgb", [0, 0, val])
    
    # Test vibration
    print("\nTesting vibration...")
    
    # Test different intensities
    print("Testing intensities...")
    for i in range(11):  # 0.0 to 1.0 in steps of 0.1
        intensity = i / 10
        print(f"Setting vibration intensity to {intensity:.1f}")
        client.send_message("/datafeel/vibration/intensity", intensity)
        client.send_message("/datafeel/vibration/frequency", 172.5)
    
    # Reset device
    print("\nResetting device...")
    client.send_message("/datafeel/device/reset", None)

def main():
    # Create OSC client
    client = udp_client.SimpleUDPClient("127.0.0.1", 8000)
    
    print("Testing Datafeel OSC Bridge...")
    
    # Test each device
    for device_id in range(1, 5):  # Test devices 1-4
        test_device(client, device_id)
    
    print("\nTest complete!")

if __name__ == "__main__":
    main()
