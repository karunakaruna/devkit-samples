"""
TouchDesigner script for sending RGB and control values to OSC Bridge.
Expects channels named:
red_N, green_N, blue_N, frequency_N
where N is the device number (1-4)
"""

import time
import json

# Configuration
MAX_DEVICES = 4
MIN_UPDATE_INTERVAL = 0.016  # 60fps
last_update = {}

def should_update(device_id):
    current_time = time.time()
    last_time = last_update.get(device_id, 0)
    if (current_time - last_time) >= MIN_UPDATE_INTERVAL:
        last_update[device_id] = current_time
        return True
    return False

def update_debug_table(device_states):
    debug_table = parent().op('debug_table')
    if not debug_table:
        return
        
    debug_table.clear()
    debug_table.appendRow(['Device', 'Red', 'Green', 'Blue', 'Vibration', 'Frequency'])
    
    for device_id in range(1, MAX_DEVICES + 1):
        state = device_states.get(str(device_id))
        if state:
            debug_table.appendRow([
                device_id,
                state['rgb'][0],
                state['rgb'][1],
                state['rgb'][2],
                f"{state['vibration']:.3f}",
                f"{state['frequency']:.3f}"
            ])
        else:
            # Append empty row for missing devices
            debug_table.appendRow([device_id, '-', '-', '-', '-', '-'])

def collect_device_states(out1):
    device_states = {}
    
    # Create a fast lookup for channel data
    channel_map = {}
    for i in range(out1.numChans):
        chan = out1.chan(i)
        channel_map[chan.name] = chan[0]
    
    # Process each device
    for device_id in range(1, MAX_DEVICES + 1):
        # Check if device exists by looking for red channel
        red_name = f'red_{device_id}'
        if red_name not in channel_map:
            continue
            
        # Get RGB values
        red_val = float(channel_map.get(red_name, 0))
        green_val = float(channel_map.get(f'green_{device_id}', 0))
        blue_val = float(channel_map.get(f'blue_{device_id}', 0))
        freq_val = float(channel_map.get(f'frequency_{device_id}', 0))
        
        # Scale values
        rgb = [
            int(max(0, min(255, val)))  # Assume values are already in 0-255 range
            for val in (red_val, green_val, blue_val)
        ]
        
        # Store device state
        device_states[str(device_id)] = {
            'rgb': rgb,
            'vibration': max(0, min(1, red_val / 255.0)),  # Normalize to 0-1
            'frequency': max(0, min(1, freq_val / 255.0))  # Normalize to 0-1
        }
    
    update_debug_table(device_states)
    return device_states

def onValueChange(channel, sampleIndex, val, prev):
    oscout = parent().op('oscout2')
    if not oscout:
        return

    out1 = parent().op('out1')
    if not out1:
        return

    try:
        # Extract device ID from channel name
        parts = channel.name.split('_')
        if len(parts) != 2 or not parts[1].isdigit():
            return
            
        device_id = int(parts[1])
        if device_id < 1 or device_id > MAX_DEVICES:
            return
        
        # Rate limit updates
        if not should_update(device_id):
            return

        # Collect and send states
        device_states = collect_device_states(out1)
        if device_states:
            oscout.par.port = 9001
            oscout.par.address = "127.0.0.1"
            
            batch_msg = {
                'timestamp': time.time(),
                'devices': device_states
            }
            
            oscout.sendOSC('/datafeel/batch_update', [json.dumps(batch_msg)])
            
    except Exception:
        pass  # Silently handle errors

def onOffToOn(channel, sampleIndex, val, prev):
    pass

def whileOn(channel, sampleIndex, val):
    pass

def onOnToOff(channel, sampleIndex, val, prev):
    pass
