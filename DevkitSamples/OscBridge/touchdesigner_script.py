"""
TouchDesigner script for sending RGB values to OSC Bridge.
Place this in text1 DAT and call it from a CHOP Execute DAT.
Requires an OSC out DAT named 'oscout2' in the same component.
"""

import time

# Track last update time per device to prevent queue buildup
last_update = {}
MIN_UPDATE_INTERVAL = 0.033  # Match bridge's 30Hz rate

def should_update(device_id):
    current_time = time.time()
    last_time = last_update.get(device_id, 0)
    if (current_time - last_time) >= MIN_UPDATE_INTERVAL:
        last_update[device_id] = current_time
        return True
    return False

def onOffToOn(channel, sampleIndex, val, prev):
    pass

def whileOn(channel, sampleIndex, val):
    pass

def onOnToOff(channel, sampleIndex, val, prev):
    pass

def onValueChange(channel, sampleIndex, val, prev):
    print(f"\n[TD->OSC] Channel '{channel.name}' changed: {prev:.3f} -> {val:.3f}")
    
    # Get the OSCout DAT
    oscout = parent().op('oscout2')
    if not oscout:
        print("[TD->OSC] Error: Cannot find OSCout DAT named 'oscout2'")
        return

    # Configure OSC parameters for the bridge
    oscout.par.address = "127.0.0.1"  # Host address
    oscout.par.port = 9001  # Bridge listens on port 9001

    try:
        # Parse channel name format: <type>_<device_id>
        channel_type, device_id = channel.name.split('_')
        device_id = int(device_id)
        print(f"[TD->OSC] Parsed channel: type={channel_type}, device={device_id}")
    except ValueError:
        print(f"[TD->OSC] Error: Invalid channel name format: {channel.name}")
        print("Channel names should be in format: <type>_<device_id> (e.g., red_1)")
        return

    # First select the device
    print(f"[TD->OSC] Selecting device {device_id}")
    oscout.sendOSC('/datafeel/device/select', [float(device_id)])

    # Get the output CHOP
    out1 = parent().op('out1')
    if not out1:
        print("[TD->OSC] Error: Cannot find CHOP named 'out1'")
        return

    # Handle different types of messages
    if channel_type in ['red', 'green', 'blue']:
        try:
            # Get current RGB values for this specific device
            current_red = float(out1[f'red_{device_id}'][0])
            current_green = float(out1[f'green_{device_id}'][0])
            current_blue = float(out1[f'blue_{device_id}'][0])
            
            print(f"[TD->OSC] Raw RGB values: {current_red:.3f}, {current_green:.3f}, {current_blue:.3f}")

            # Scale values from 0-1 to 0-255
            rgb_values = [
                int(current_red * 255),
                int(current_green * 255),
                int(current_blue * 255)
            ]
            
            # Update the changed value
            if channel_type == 'red':
                rgb_values[0] = int(val * 255)
            elif channel_type == 'green':
                rgb_values[1] = int(val * 255)
            elif channel_type == 'blue':
                rgb_values[2] = int(val * 255)

            # Clamp values to 0-255 range
            rgb_values = [max(0, min(255, v)) for v in rgb_values]
            
            print(f"[TD->OSC] Sending RGB: {rgb_values}")
            oscout.sendOSC('/datafeel/led/rgb', rgb_values)

            # If this is a red channel update, also update vibration
            if channel_type == 'red':
                vibration = val  # Already in 0-1 range
                print(f"[TD->OSC] Sending vibration intensity: {vibration:.3f}")
                oscout.sendOSC('/datafeel/vibration/intensity', [vibration])

        except Exception as e:
            print(f"[TD->OSC] Error processing RGB values: {str(e)}")
            return

    elif channel_type == 'frequency':
        # Ensure frequency is in 0-1 range
        frequency = max(0, min(1, float(val)))
        print(f"[TD->OSC] Sending frequency: {frequency:.3f}")
        oscout.sendOSC('/datafeel/vibration/frequency', [frequency])
