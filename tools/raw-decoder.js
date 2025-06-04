import fs from 'fs';
import { WebSocketServer } from 'ws';

const FILE = process.argv[2] || 'sample-raw.txt';
const PORT = 5221;
const PATH = '/ws';

const wss = new WebSocketServer({ port: PORT, path: PATH });
const clients = new Set();

wss.on('connection', ws => {
  clients.add(ws);
  ws.on('close', () => clients.delete(ws));
});

function parseLine(line) {
  const obj = {};
  line.trim().split(/\s+/).forEach(kv => {
    const [key, val] = kv.split(':');
    if (key) obj[key] = Number(val);
  });
  return obj;
}

let lastLap = null;
let lastFuel = null;
let totalFuelUsed = 0;
let lapCount = 0;

function calcExtras(t) {
  const fuelUse = t.FuelPerLap || 0;
  const fuelLeft = t.FuelLevel || 0;

  if (typeof t.Lap === 'number') {
    if (lastLap !== null && t.Lap !== lastLap) {
      const diff = lastFuel !== null ? lastFuel - fuelLeft : 0;
      if (diff > 0) {
        totalFuelUsed += diff;
        lapCount += 1;
      }
    }
    lastLap = t.Lap;
  }
  if (typeof fuelLeft === 'number') {
    lastFuel = fuelLeft;
  }

  const avgFuel = lapCount > 0 ? totalFuelUsed / lapCount : 0;

  t.CurrentFuelPerLap = fuelUse;
  t.AvgFuelPerLap = avgFuel;
  t.LapsRemainingCur = fuelUse > 0 ? fuelLeft / fuelUse : 0;
  t.LapsRemainingAvg = avgFuel > 0 ? fuelLeft / avgFuel : 0;

  t.SectorTimes = [t.S1, t.S2, t.S3].filter(v => typeof v === 'number');

  return t;
}

function broadcast(data) {
  const payload = JSON.stringify(data);
  for (const ws of clients) {
    if (ws.readyState === ws.OPEN) ws.send(payload);
  }
}

function watch() {
  if (!fs.existsSync(FILE)) {
    console.error(`File ${FILE} not found`);
    return;
  }
  console.log(`Watching ${FILE} and serving WebSocket ws://localhost:${PORT}${PATH}`);
  let lastSize = 0;
  fs.watchFile(FILE, () => {
    const stat = fs.statSync(FILE);
    if (stat.size === lastSize) return;
    lastSize = stat.size;
    const lines = fs.readFileSync(FILE, 'utf8').trim().split(/\r?\n/);
    const raw = parseLine(lines[lines.length - 1]);
    const data = calcExtras(raw);
    broadcast(data);
  });
}

watch();
