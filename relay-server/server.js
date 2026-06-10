// FFXIV-TV Relay Server
// Both host and clients connect outbound — no port forwarding required on either end.
//
// Host:   WS /host          → receives { type:"room", code:"AB12CD" }
// Client: WS /join/:code    → relays host messages to this client
//
// When a client joins:
//   relay sends { type:"joined" } to the host
//   host responds by broadcasting screen + state to all clients (new client catches it)
//
// Deploy: fly.io / Railway / Render — anything that supports Node + WebSocket.

const http    = require('http');
const crypto  = require('crypto');
const { WebSocketServer, WebSocket } = require('ws');

const PORT  = process.env.PORT || 8080;
const rooms = new Map(); // code -> { hostWs, clients: Set<WebSocket>, createdAt }

// ── Room code generation ───────────────────────────────────────────────────────
function makeCode() {
    let code;
    do { code = crypto.randomBytes(3).toString('hex').toUpperCase(); }
    while (rooms.has(code));
    return code;
}

// ── HTTP server (health check + room count) ───────────────────────────────────
const httpServer = http.createServer((req, res) => {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ service: 'ffxiv-tv-relay', rooms: rooms.size }));
});

// ── WebSocket server ──────────────────────────────────────────────────────────
const wss = new WebSocketServer({ server: httpServer });

wss.on('connection', (ws, req) => {
    const url      = new URL(req.url, 'http://x');
    const path     = url.pathname;
    const joinMatch = path.match(/^\/join\/([A-Fa-f0-9]{6})$/);

    if (path === '/host') {
        handleHost(ws);
    } else if (joinMatch) {
        handleClient(ws, joinMatch[1].toUpperCase());
    } else {
        ws.close(4000, 'Unknown path');
    }
});

// ── Host handler ──────────────────────────────────────────────────────────────
function handleHost(ws) {
    const code = makeCode();
    const room = { hostWs: ws, clients: new Set(), createdAt: Date.now() };
    rooms.set(code, room);

    // Tell the host its room code immediately.
    send(ws, { type: 'room', code });
    console.log(`[relay] Room ${code} created`);

    ws.on('message', (data) => {
        // Forward every host message to all connected clients verbatim.
        for (const client of room.clients) {
            if (client.readyState === WebSocket.OPEN)
                client.send(data);
        }
    });

    ws.on('close', () => {
        // Notify any lingering clients and clean up.
        for (const client of room.clients) {
            if (client.readyState === WebSocket.OPEN)
                send(client, { type: 'host_disconnected' });
        }
        rooms.delete(code);
        console.log(`[relay] Room ${code} closed`);
    });

    ws.on('error', (err) => console.warn(`[relay] Host error (${code}): ${err.message}`));
}

// ── Client handler ────────────────────────────────────────────────────────────
function handleClient(ws, code) {
    const room = rooms.get(code);
    if (!room) {
        send(ws, { type: 'error', message: 'Room not found' });
        ws.close(4004, 'Room not found');
        return;
    }

    room.clients.add(ws);
    console.log(`[relay] Client joined room ${code} (${room.clients.size} total)`);

    // Tell the host a client joined so it pushes current state.
    if (room.hostWs.readyState === WebSocket.OPEN)
        send(room.hostWs, { type: 'joined' });

    ws.on('close', () => {
        room.clients.delete(ws);
        console.log(`[relay] Client left room ${code} (${room.clients.size} remaining)`);
    });

    ws.on('error', (err) => console.warn(`[relay] Client error (${code}): ${err.message}`));
}

// ── Helpers ───────────────────────────────────────────────────────────────────
function send(ws, obj) {
    if (ws.readyState === WebSocket.OPEN)
        ws.send(JSON.stringify(obj));
}

// ── Cleanup stale rooms every 30 minutes ─────────────────────────────────────
setInterval(() => {
    const cutoff = Date.now() - 30 * 60 * 1000;
    for (const [code, room] of rooms) {
        if (room.createdAt < cutoff && room.clients.size === 0) {
            try { room.hostWs.close(); } catch {}
            rooms.delete(code);
            console.log(`[relay] Pruned stale room ${code}`);
        }
    }
}, 30 * 60 * 1000);

httpServer.listen(PORT, () => console.log(`[relay] FFXIV-TV relay listening on port ${PORT}`));
