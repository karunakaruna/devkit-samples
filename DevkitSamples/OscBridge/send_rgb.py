"""
TouchDesigner script for sending RGB values to OSC Bridge.
Place this in text1 DAT and call it from a CHOP Execute DAT.
Requires an OSC out DAT named 'oscout2' in the same component.
"""

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
    oscout.par.host = "127.0.0.1"  # Host address
    oscout.par.port = 8000  # Bridge listens on port 8000
    
    # Map channel names to OSC addresses
    osc_map = {
        'red': ('/datafeel/led/rgb', lambda v: [int(v * 255), None, None]),
        'green': ('/datafeel/led/rgb', lambda v: [None, int(v * 255), None]),
        'blue': ('/datafeel/led/rgb', lambda v: [None, None, int(v * 255)]),
        'intensity': ('/datafeel/vibration/intensity', lambda v: [v]),
        'frequency': ('/datafeel/vibration/frequency', lambda v: [v]),
        'device': ('/datafeel/device/select', lambda v: [v])
    }

    # Check if this channel is mapped
    if channel.name in osc_map:
        address, value_mapper = osc_map[channel.name]
        
        # For RGB, we need to get all current values
        if address == '/datafeel/led/rgb':
            out1 = parent().op('out1')
            if not out1:
                print("[TD->OSC] Error: Cannot find CHOP named 'out1'")
                return
                
            current_red = float(out1['red'][0])
            current_green = float(out1['green'][0])
            current_blue = float(out1['blue'][0])
            
            # Create the RGB message with all three values
            rgb_values = [
                int(current_red * 255),
                int(current_green * 255),
                int(current_blue * 255)
            ]
            
            # Update the changed value
            if channel.name == 'red':
                rgb_values[0] = int(val * 255)
            elif channel.name == 'green':
                rgb_values[1] = int(val * 255)
            elif channel.name == 'blue':
                rgb_values[2] = int(val * 255)
            
            print(f"[TD->OSC] Sending RGB: {rgb_values}")
            oscout.sendOSC(address, rgb_values)
        else:
            # For non-RGB values, just send the single value
            values = value_mapper(val)
            values = [v if v is not None else 0 for v in values]  # Replace None with 0
            print(f"[TD->OSC] Sending {address}: {values}")
            oscout.sendOSC(address, values)
