// Ephemeral CI only: real HTTPS + actual signed installer bytes. Not shipped in any product package.
if(process.env.GITHUB_ACTIONS!=='true')throw new Error('CI fixture cannot run in production');
const https=require('node:https'),fs=require('node:fs'),crypto=require('node:crypto');
const id=crypto.randomUUID(),credential=crypto.randomBytes(32).toString('hex').toUpperCase();
let enrolled=false;const result={receipts:[],versions:[]};
function save(){fs.writeFileSync(process.env.UPDATE_RESULT,JSON.stringify(result));}
const tls={pfx:fs.readFileSync(process.env.UPDATE_PFX),passphrase:process.env.UPDATE_PASSWORD};
const consoleServer=https.createServer(tls,(req,res)=>{
 let text='';req.on('data',x=>{text+=x;if(text.length>262144)req.destroy();});req.on('end',()=>{
  res.setHeader('content-type','application/json');
  if(req.url==='/health'){res.end('{}');return;}
  let body;try{body=text?JSON.parse(text):null;}catch{res.writeHead(400);res.end();return;}
  if(req.url==='/api/v1/agent/enroll'&&req.method==='POST'&&!enrolled&&body?.token===process.env.UPDATE_TOKEN){enrolled=true;res.end(JSON.stringify({deviceId:id,credential}));return;}
  if(req.headers.authorization!==`Bearer ${credential}`||req.headers['x-device-id']!==id){res.writeHead(401);res.end();return;}
  if(req.url==='/api/v1/agent/heartbeat'){if(body?.engineReachable)result.versions.push(body.agentVersion);save();res.writeHead(204);res.end();return;}
  const stage=JSON.parse(fs.readFileSync(process.env.UPDATE_STAGE));
  if(req.url==='/api/v1/agent/update'){
   if(!stage.assignment){res.writeHead(204);res.end();return;}
   if(result.receipts.some(x=>x.phase===stage.phase&&['Rejected','Failed','Installed','Downloaded','Applying'].includes(x.status))){res.writeHead(204);res.end();return;}
   res.end(JSON.stringify(stage.assignment));return;
  }
  if(req.method==='POST'&&req.url===`/api/v1/agent/updates/${stage.assignment?.deploymentId}/receipt`){result.receipts.push({...body,phase:stage.phase});save();res.writeHead(204);res.end();return;}
  res.writeHead(204);res.end();
 });
});
const artifacts=https.createServer(tls,(req,res)=>{
 if(req.headers.authorization)throw new Error('Console credentials leaked to artifact server');
 const stage=JSON.parse(fs.readFileSync(process.env.UPDATE_STAGE));const bytes=fs.readFileSync(process.env.UPDATE_INSTALLER);
 if(stage.tamper)bytes[bytes.length-100]^=1;
 res.setHeader('content-length',bytes.length);res.setHeader('content-type','application/octet-stream');res.end(bytes);
});
consoleServer.listen(18444,'127.0.0.1');artifacts.listen(443,'127.0.0.1');
