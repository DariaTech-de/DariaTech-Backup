// CI-only HTTPS fixture using independently signed real native packages.
const https=require('node:https'),fs=require('node:fs'),crypto=require('node:crypto');
const tls={key:fs.readFileSync(process.env.FIXTURE_SERVER_KEY),cert:fs.readFileSync(process.env.FIXTURE_SERVER_CERT),ca:fs.readFileSync(process.env.FIXTURE_CA)};
const bytes=fs.readFileSync(process.env.UPDATE_ARTIFACT),key=fs.readFileSync(process.env.UPDATE_PRIVATE_KEY);
const device=crypto.randomUUID(),credential=crypto.randomBytes(32).toString('hex').toUpperCase();
const release=crypto.randomUUID(),deployments={bad:crypto.randomUUID(),tampered:crypto.randomUUID(),good:crypto.randomUUID()};
const result={enrollments:0,heartbeats:0,receipts:[],versions:[]};function save(){fs.writeFileSync(process.env.FIXTURE_RESULT,JSON.stringify(result));}
function phase(){return fs.readFileSync(process.env.UPDATE_PHASE,'utf8').trim();}
function assignment(){
 const p=phase(),now=new Date(),expires=new Date(now.getTime()+86400000);
 const manifest={ReleaseId:release,Sequence:p==='bad'?1:p==='tampered'?2:3,Product:'DariaTechBackupAgent',Platform:process.env.UPDATE_PLATFORM,Version:'0.3.0.0',ArtifactUrl:'https://localhost/agent.tar.gz',Sha256:crypto.createHash('sha256').update(bytes).digest('hex'),Length:bytes.length,Issued:now.toISOString(),Expires:expires.toISOString()};
 const payload=Buffer.from(JSON.stringify(manifest)),signature=crypto.sign('sha256',payload,{key,dsaEncoding:'ieee-p1363'});
 if(p==='bad')signature[0]^=1;
 return {deploymentId:deployments[p],manifest:{payload:payload.toString('base64'),signature:signature.toString('base64')}};
}
https.createServer(tls,(req,res)=>{
 if(req.url==='/health'){res.end('{}');return;}
 if(req.url==='/api/v1/agent/update'&&req.method==='GET'){
  if(req.headers.authorization!=='Bearer '+credential){res.writeHead(401);res.end('{}');return;}
  if(result.receipts.some(r=>r.phase===phase()&&['Installed','Failed','Rejected'].includes(r.status))){res.writeHead(204);res.end();return;}
  res.setHeader('content-type','application/json');res.end(JSON.stringify(assignment()));return;
 }
 let text='';req.on('data',data=>{text+=data;if(text.length>262144)req.destroy();});req.on('end',()=>{
  res.setHeader('content-type','application/json');let body;try{body=JSON.parse(text);}catch{res.writeHead(400);res.end('{}');return;}
  if(req.url==='/api/v1/agent/enroll'&&req.method==='POST'&&result.enrollments===0&&body.token===process.env.FIXTURE_TOKEN){result.enrollments++;save();res.end(JSON.stringify({deviceId:device,credential}));return;}
  if(req.headers.authorization!=='Bearer '+credential||req.headers['x-device-id']!==device){res.writeHead(401);res.end('{}');return;}
  if(req.url==='/api/v1/agent/heartbeat'){if(body.engineReachable)result.heartbeats++;result.versions.push(body.agentVersion);save();res.end('{}');return;}
  if(/^\/api\/v1\/agent\/updates\/[0-9a-f-]+\/receipt$/.test(req.url)){
   result.receipts.push({phase:phase(),status:body.status,errorCode:body.errorCode});save();res.writeHead(204);res.end();return;
  }
  res.writeHead(403);res.end('{}');
 });
}).listen(18443,'127.0.0.1');
https.createServer(tls,(req,res)=>{
 if(req.url!=='/agent.tar.gz'||req.headers.authorization){res.writeHead(403);res.end();return;}
 let artifact=bytes;if(phase()==='tampered'){artifact=Buffer.from(bytes);artifact[0]^=1;}
 res.setHeader('content-length',artifact.length);res.end(artifact);
}).listen(443,'127.0.0.1');
