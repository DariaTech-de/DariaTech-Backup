// CI-only HTTPS fixture. The actual PostgreSQL Console is covered by separate tests.
const https=require('node:https'),fs=require('node:fs'),crypto=require('node:crypto');
const device=crypto.randomUUID(),credential=crypto.randomBytes(32).toString('hex').toUpperCase();
let enrollments=0,heartbeats=0;
function record(){fs.writeFileSync(process.env.FIXTURE_RESULT,JSON.stringify({enrolled:enrollments===1,enrollments,heartbeats}));}
https.createServer({pfx:fs.readFileSync(process.env.FIXTURE_PFX),passphrase:process.env.FIXTURE_PASSWORD},(req,res)=>{
 if(req.url==='/health'){res.end('{}');return;}
 let text='';req.on('data',data=>{text+=data;if(text.length>262144)req.destroy();});req.on('end',()=>{
  res.setHeader('content-type','application/json');
  let body;try{body=JSON.parse(text);}catch{res.writeHead(400);res.end('{}');return;}
  if(req.url==='/api/v1/agent/enroll'&&req.method==='POST'&&enrollments===0&&body.token===process.env.FIXTURE_TOKEN){
   enrollments++;record();res.end(JSON.stringify({deviceId:device,credential}));return;
  }
  if(req.url==='/api/v1/agent/heartbeat'&&req.method==='POST'&&req.headers.authorization==='Bearer '+credential&&req.headers['x-device-id']===device){
   if(text.includes(process.env.FIXTURE_ENGINE_PASSWORD)){res.writeHead(400);res.end('{}');return;}
   if(body.engineReachable)heartbeats++;record();res.end('{}');return;
  }
  res.writeHead(403);res.end('{}');
 });
}).listen(18443,'127.0.0.1');
