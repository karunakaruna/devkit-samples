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
last_update = {}

def should_update(device_id):
    export_parms = parent().op('export_parms')
    if not export_parms:
        return False
        
    # Get framerate from export_parms and calculate minimum interval
    framerate = float(export_parms['framerate'])
    if framerate <= 0:
        framerate = 60  # fallback to 60fps if invalid
    min_update_interval = 1.0 / framerate
    
    current_time = time.time()
    last_time = last_update.get(device_id, 0)
    if (current_time - last_time) >= min_update_interval:
        last_update[device_id] = current_time
        return True
    return False