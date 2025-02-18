"""
TouchDesigner script for sending RGB and control values to OSC Bridge.
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

def onValueChange(channel, sampleIndex, val, prev):
    # Get the OSCout DAT
    oscout = parent().op('oscout2')
    if not oscout:
        print("[TD->OSC] Error: Cannot find OSCout DAT named 'oscout2'")
        return

    # Configure OSC parameters
    oscout.par.port = 8000
    oscout.par.address = "127.0.0.1"

    # Get the output CHOP
    out1 = parent().op('out1')
    if not out1:
        print("[TD->OSC] Error: Cannot find CHOP named 'out1'")
        return

    try:
        # Parse channel name format: <type>_<device_id>
        channel_type, device_id = channel.name.split('_')
    except ValueError:
        print(f"[TD->OSC] Error: Invalid channel name format: {channel.name}")
        print("Channel names should be in format: <type>_<device_id> (e.g., red_1)")
        return

    # Only process if enough time has passed
    if not should_update(device_id):
        return

    # Handle different types of messages
    if channel_type in ['red', 'green', 'blue']:
        # Get current RGB values for this specific device
        try:
            current_red = out1[f'red_{device_id}'][0]
            current_green = out1[f'green_{device_id}'][0]
            current_blue = out1[f'blue_{device_id}'][0]
        except:
            print(f"[TD->OSC] Error: Cannot find RGB channels for device {device_id}")
            return

        # Send device selection and RGB in one batch
        msg_data = [float(current_red), float(current_green), float(current_blue)]
        print(f"[TD->OSC] Device {device_id} sending RGB: {msg_data}")
        
        # Select device and send RGB
        oscout.sendOSC('/datafeel/device/select', [float(device_id)])
        oscout.sendOSC('/datafeel/led/rgb', msg_data)

        # Send vibration based on red value if this is a red update
        if channel_type == 'red':
            vibration = float(val) / 255.0
            print(f"[TD->OSC] Device {device_id} sending vibration: {vibration:.3f}")
            oscout.sendOSC('/datafeel/vibration/intensity', [vibration])

    elif channel_type == 'frequency':
        # Select device and send frequency
        oscout.sendOSC('/datafeel/device/select', [float(device_id)])
        msg_data = [float(val)]
        print(f"[TD->OSC] Device {device_id} sending frequency: {msg_data}")
        oscout.sendOSC('/datafeel/vibration/frequency', msg_data)

def onOffToOn(channel, sampleIndex, val, prev):
    pass

def whileOn(channel, sampleIndex, val):
    pass

def onOnToOff(channel, sampleIndex, val, prev):
    pass
