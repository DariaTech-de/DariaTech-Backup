// Test-only HTTPS enrollment/telemetry fixture. Never shipped in the installer.
const https = require('node:https');
const fs = require('node:fs');
const crypto = require('node:crypto');
const id = crypto.randomUUID();
const credential = crypto.randomBytes(32).toString('hex').toUpperCase();
let enrolled = false, heartbeats = 0;
const server = https.createServer({pfx:fs.readFileSync(process.env.FIXTURE_PFX),passphrase:process.env.FIXTURE_PASSWORD},(req,res)=>{
 let text='';
 req.on('data',chunk=>{text+=chunk;if(text.length>262144)req.destroy();});
 req.on('end',()=>{
  res.setHeader('content-type','application/json');
  if(req.url==='/health'){res.end('{}');return;}
  let body;try{body=JSON.parse(text);}catch{res.writeHead(400);res.end('{}');return;}
  if(req.url==='/api/v1/agent/enroll'&&req.method==='POST'&&!enrolled&&body.token===process.env.FIXTURE_TOKEN){
   if(Object.keys(body).sort().join(',')!=='agentVersion,hostname,operatingSystem,platform,token'||body.platform!=='win-x64'){res.writeHead(400);res.end('{}');return;}
   enrolled=true;res.end(JSON.stringify({deviceId:id,credential}));return;
  }
  if(req.url==='/api/v1/agent/heartbeat'&&req.method==='POST'&&req.headers.authorization===`Bearer ${credential}`&&req.headers['x-device-id']===id){
   if(text.includes(process.env.FIXTURE_ENGINE_PASSWORD)){res.writeHead(400);res.end('{}');return;}
   if(body.engineReachable)heartbeats++;
   fs.writeFileSync(process.env.FIXTURE_RESULT,JSON.stringify({enrolled,heartbeats}));res.end('{}');return;
  }
  res.writeHead(403);res.end('{}');
 });
});
server.listen(18443,'127.0.0.1');
