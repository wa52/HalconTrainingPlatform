// Serve the training workspace only; never expose the project directory.
const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const ui = path.resolve(__dirname, '../docs/UI_DESIGN.html');
http.createServer(async (req, res) => {
  if(req.method !== 'GET' || req.url !== '/') { res.writeHead(404); res.end(); return; }
  try { res.setHeader('Content-Type','text/html; charset=utf-8'); res.setHeader('Cache-Control','no-store'); res.end(await fs.readFile(ui)); }
  catch { res.writeHead(500); res.end('界面加载失败'); }
}).listen(8768,'127.0.0.1',()=>process.stdout.write('训练界面：http://127.0.0.1:8768/\n'));
