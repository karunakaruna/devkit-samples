from aiohttp import web, WSMsgType
from pythonosc.osc_server import AsyncIOOSCUDPServer
from pythonosc.udp_client import SimpleUDPClient
from pythonosc.dispatcher import Dispatcher
import asyncio
import json
import logging
from datetime import datetime
from typing import List, Dict
import os
import webbrowser

# Configure logging
logging.basicConfig(level=logging.WARNING)
logger = logging.getLogger(__name__)

# Store active WebSocket connections
active_connections: List[web.WebSocketResponse] = []

# Message history for new connections
message_history: List[Dict] = []
MAX_HISTORY = 100

# OSC client for forwarding messages to the bridge
osc_client = None

# Rate limiting settings
MIN_UPDATE_INTERVAL = 1/60  # 60Hz max update rate
last_update_time = 0
pending_messages = {}  # Store the latest message for each address

def should_process_message(address: str) -> bool:
    """Check if we should process a message based on rate limiting."""
    global last_update_time
    current_time = asyncio.get_event_loop().time()
    time_since_last_update = current_time - last_update_time
    
    if time_since_last_update >= MIN_UPDATE_INTERVAL:
        last_update_time = current_time
        return True
    return False

async def process_pending_messages():
    """Process any pending messages that have been queued."""
    global pending_messages
    if pending_messages and should_process_message("batch"):
        messages_to_send = pending_messages.copy()
        pending_messages.clear()
        
        for address, args in messages_to_send.items():
            message = {
                "type": "osc_message",
                "address": address,
                "args": args
            }
            await broadcast_to_websockets(message)

async def broadcast_to_websockets(message: dict):
    """Broadcast message to all connected WebSocket clients."""
    if len(active_connections) > 0:
        message_with_timestamp = {
            **message,
            "timestamp": datetime.now().isoformat()
        }
        message_history.append(message_with_timestamp)
        if len(message_history) > MAX_HISTORY:
            message_history.pop(0)
            
        for ws in active_connections[:]:  # Create a copy of the list
            try:
                await ws.send_json(message_with_timestamp)
            except Exception as e:
                logger.error(f"Error sending to WebSocket: {e}")
                active_connections.remove(ws)

async def handle_osc_message(address: str, *args):
    """Handle incoming OSC messages with rate limiting."""
    if should_process_message(address):
        # Convert all arguments to float for consistency
        float_args = [float(arg) for arg in args]
        print(f"[OSC] Forwarding: {address} {float_args}")
        
        # Forward to the C# bridge
        if osc_client:
            osc_client.send_message(address, float_args)
        
        # Broadcast to WebSocket clients
        await broadcast_to_websockets({
            'type': 'osc_message',
            'address': address,
            'args': float_args
        })

async def websocket_handler(request):
    """Handle WebSocket connections."""
    ws = web.WebSocketResponse()
    await ws.prepare(request)
    
    active_connections.append(ws)
    logger.info(f"New WebSocket connection. Total connections: {len(active_connections)}")
    print(f"\n[WS] New client connected from {request.remote}")
    
    try:
        async for msg in ws:
            if msg.type == WSMsgType.TEXT:
                try:
                    data = json.loads(msg.data)
                    print(f"[WS->OSC] Received: {data}")
                    
                    if data['type'] == 'osc_message':
                        # Forward the OSC message
                        address = data['address']
                        args = [float(arg) for arg in data['args']]  # Ensure args are floats
                        print(f"[WS->OSC] Forwarding: {address} {args}")
                        if osc_client:
                            osc_client.send_message(address, args)
                except Exception as e:
                    logger.error(f"Error processing message: {e}")
                    print(f"[WS] Error processing message: {str(e)}")
            elif msg.type == WSMsgType.ERROR:
                logger.error(f"WebSocket connection closed with exception {ws.exception()}")
    finally:
        active_connections.remove(ws)
        logger.info(f"WebSocket disconnected. Remaining connections: {len(active_connections)}")
        print(f"[WS] Client disconnected: {request.remote}")
    
    return ws

async def index_handler(request):
    """Serve the monitor.html file."""
    return web.FileResponse('monitor.html')

async def try_create_osc_server(dispatcher, base_port=8001, max_attempts=5):
    """Try to create OSC server on available port."""
    for port in range(base_port, base_port + max_attempts):
        try:
            osc_server = AsyncIOOSCUDPServer(
                ("127.0.0.1", port),
                dispatcher,
                asyncio.get_running_loop()
            )
            transport, protocol = await osc_server.create_serve_endpoint()
            return transport, protocol, port
        except OSError as e:
            if port < base_port + max_attempts - 1:
                logger.warning(f"Port {port} in use, trying next port...")
                continue
            raise e
    raise RuntimeError("Could not find available port for OSC server")

async def try_create_websocket_server(runner, base_port=8081, max_attempts=5):
    """Try to create WebSocket server on available port."""
    for port in range(base_port, base_port + max_attempts):
        try:
            site = web.TCPSite(runner, '127.0.0.1', port)
            await site.start()
            return site, port
        except OSError as e:
            if port < base_port + max_attempts - 1:  # Don't log if it's the last attempt
                logger.warning(f"Port {port} in use, trying next port...")
            else:
                raise  # Re-raise the exception if we've exhausted all ports
    raise OSError(f"Could not find available port in range {base_port}-{base_port + max_attempts - 1}")

async def main():
    """Main entry point."""
    global osc_client
    
    try:
        # Initialize OSC client for the C# bridge
        osc_client = SimpleUDPClient("127.0.0.1", 8000)  # Bridge listens on port 8000
        
        # Set up OSC server
        dispatcher = Dispatcher()
        dispatcher.set_default_handler(handle_osc_message)
        
        # Try to create OSC server with port retry
        transport, protocol, osc_port = await try_create_osc_server(dispatcher)
        
        # Set up HTTP/WebSocket server
        app = web.Application()
        app.router.add_get('/', index_handler)
        app.router.add_get('/ws', websocket_handler)
        
        # Configure CORS
        app.router.add_options('/{tail:.*}', handle_options_request)
        app.on_response_prepare.append(on_prepare_response)
        
        runner = web.AppRunner(app)
        await runner.setup()
        
        # Try to create WebSocket server with port retry
        site, ws_port = await try_create_websocket_server(runner)
        
        logger.warning("OSC Bridge started:")  
        logger.warning(f"  - OSC server listening on 127.0.0.1:{osc_port}")
        logger.warning(f"  - Web interface available at http://localhost:{ws_port}")
        
        # Open test.html in the default browser with the WebSocket port as a query parameter
        test_html_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'test.html')
        webbrowser.open(f'file://{test_html_path}?ws_port={ws_port}')
        
        # Main update loop
        while True:
            await process_pending_messages()  # Process any queued messages
            await asyncio.sleep(MIN_UPDATE_INTERVAL)  # Wait for next frame
            
    except KeyboardInterrupt:
        logger.info("Shutting down servers...")
    except Exception as e:
        logger.error(f"Error: {e}")
        raise
    finally:
        if 'transport' in locals():
            transport.close()
        if 'runner' in locals():
            await runner.cleanup()

async def handle_options_request(request):
    return web.Response(status=204)

async def on_prepare_response(request, response):
    response.headers['Access-Control-Allow-Origin'] = '*'
    response.headers['Access-Control-Allow-Methods'] = 'GET, POST, OPTIONS'
    response.headers['Access-Control-Allow-Headers'] = 'Content-Type'

if __name__ == "__main__":
    asyncio.run(main())
