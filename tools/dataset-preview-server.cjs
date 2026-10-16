// Local UI-design bridge. HALCON parsing runs in a short-lived worker process.
const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFile } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const images = new Map();
const modelDirectories = new Map();
const testDirectories = new Map();
let testing = false;
const allowedOrigins = new Set(['http://127.0.0.1:8768']);
http.createServer(async (req, res) => {
  const origin = req.headers.origin;
  if (origin && !allowedOrigins.has(origin)) { res.writeHead(403); res.end(); return; }
  if (origin) res.setHeader('Access-Control-Allow-Origin', origin);
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type, X-Dataset-Name, X-Image-Name, X-Model-Id, X-Test-Id');
  res.setHeader('Access-Control-Allow-Methods', 'POST, GET, OPTIONS');
  if (req.method === 'OPTIONS') { res.writeHead(204); res.end(); return; }
  let cache;
  let ownsTest = false;
  try {
    if(req.method==='POST' && req.url==='/save-overlay' && origin) {
      const dir=testDirectories.get(req.headers['x-test-id']);
      if(!dir)throw new Error('测试结果已失效，请重新测试原图。');
      const chunks=[];let size=0;
      for await(const chunk of req){size+=chunk.length;if(size>64*1024*1024)throw new Error('叠加图超过64MB。');chunks.push(chunk);}
      const png=Buffer.concat(chunks);
      if(!png.subarray(0,8).equals(Buffer.from([137,80,78,71,13,10,26,10])))throw new Error('叠加图必须为PNG。');
      const file=path.join(dir,'overlay.png');await fs.writeFile(file,png);
      res.setHeader('Content-Type','application/json; charset=utf-8');res.end(JSON.stringify({file}));return;
    }
    if (req.method === 'GET' && req.url === '/training-status') {
      const library = path.join(root,'models','semantic_segmentation');
      const candidates=[];
      for(const dataset of await fs.readdir(library,{withFileTypes:true}).catch(()=>[])) {
        if(!dataset.isDirectory())continue;
        for(const run of await fs.readdir(path.join(library,dataset.name),{withFileTypes:true})) {
          if(!run.isDirectory())continue;
          const dir=path.join(library,dataset.name,run.name);
          const log=path.join(dir,'training.log');
          const stat=await fs.stat(log).catch(()=>null);
          if(stat)candidates.push({dir,log,mtime:stat.mtimeMs,name:dataset.name});
        }
      }
      candidates.sort((a,b)=>b.mtime-a.mtime);
      if(!candidates.length){res.setHeader('Content-Type','application/json');res.end(JSON.stringify({status:'idle'}));return;}
      const run=candidates[0];
      const lines=(await fs.readFile(run.log,'utf8')).split(/\r?\n/).filter(Boolean);
      const events=lines.map(line=>{try{return JSON.parse(line);}catch{return null;}}).filter(Boolean);
      const error=lines.find(line=>line.includes('Exception:')||line.includes('HALCON error'));
      const complete=events.some(event=>event.stage==='训练完成');
      const evaluation=await fs.readFile(path.join(run.dir,'validation_report.json'),'utf8').then(JSON.parse).catch(()=>null);
      const modelId=crypto.createHash('sha256').update(run.dir).digest('hex');
      if(complete)modelDirectories.set(modelId,run.dir);
      res.setHeader('Content-Type','application/json; charset=utf-8');
      res.end(JSON.stringify({status:error?'failed':complete?'completed':'running',name:run.name,modelId,output:run.dir,evaluation,startedAt:events[0]?.time,error,events:events.slice(-200)}));return;
    }
    if(req.method==='POST' && req.url==='/test' && origin) {
      if(testing){res.writeHead(409,{'Content-Type':'application/json'});res.end(JSON.stringify({error:'已有图片正在测试，请稍候。'}));return;}
      const modelDir=modelDirectories.get(req.headers['x-model-id']);
      if(!modelDir)throw new Error('请先选择已训练的模型。');
      const filename=path.basename(decodeURIComponent(req.headers['x-image-name']||''));
      if(!/\.(png|jpg|jpeg|bmp|tif|tiff)$/i.test(filename))throw new Error('支持PNG、JPEG、BMP、TIFF图片。');
      testing=true;ownsTest=true;
      const chunks=[];let size=0;
      for await(const chunk of req){size+=chunk.length;if(size>32*1024*1024)throw new Error('单张图片超过32MB。');chunks.push(chunk);}
      const cacheRoot=path.join(root,'artifacts','test-input');await fs.mkdir(cacheRoot,{recursive:true});
      cache=await fs.mkdtemp(path.join(cacheRoot,'image-'));
      const input=path.join(cache,filename);await fs.writeFile(input,Buffer.concat(chunks));
      const outputDir=path.join(root,'results','tests',path.basename(path.dirname(modelDir)),crypto.randomUUID());
      await fs.mkdir(outputDir,{recursive:true});
      const worker=path.join(root,'src','HalconTraining.Worker','bin','Release','net8.0-windows','HalconTraining.Worker.exe');
      let result;
      try {
        const output=await new Promise((resolve,reject)=>execFile(worker,['--semantic-test',input,'--model-dir',modelDir,'--output',outputDir],{timeout:180000,maxBuffer:4*1024*1024,windowsHide:true},(error,stdout,stderr)=>error?reject(new Error(stderr.trim()||error.message)):resolve(stdout)));
        result=JSON.parse(output);
      } catch(error) { await fs.writeFile(path.join(outputDir,'error.json'),JSON.stringify({file:filename,error:error.message}));throw error; }
      result.testId=crypto.randomUUID();testDirectories.set(result.testId,outputDir);
      for(const [field,file] of [['originalUrl','original.png'],['maskUrl','mask.png']]){const token=crypto.randomUUID();images.set(token,path.join(outputDir,file));result[field]='http://127.0.0.1:8770/image/'+token;}
      res.setHeader('Content-Type','application/json; charset=utf-8');res.end(JSON.stringify(result));return;
    }
    if (req.method === 'GET' && req.url.startsWith('/image/')) {
      const file = images.get(req.url.slice(7));
      if (!file) { res.writeHead(404); res.end(); return; }
      const types = {'.png':'image/png','.jpg':'image/jpeg','.jpeg':'image/jpeg','.bmp':'image/bmp','.webp':'image/webp'};
      const type = types[path.extname(file).toLowerCase()];
      if (!type) throw new Error('浏览器暂不支持该原图格式。');
      res.setHeader('Content-Type', type); res.end(await fs.readFile(file)); return;
    }
    if (req.method !== 'POST' || req.url !== '/inspect' || !origin) { res.writeHead(404); res.end(); return; }
    const filename = path.basename(decodeURIComponent(req.headers['x-dataset-name'] || ''));
    if (!/\.(hdict|json)$/i.test(filename)) throw new Error('请选择 .hdict 或 .json。');
    const chunks = []; let size = 0;
    for await (const chunk of req) {
      size += chunk.length;
      if (size > 128 * 1024 * 1024) throw new Error('数据集字典超过 128 MB，需使用桌面程序读取。');
      chunks.push(chunk);
    }
    const cacheRoot = path.join(root,'artifacts','dataset-preview');
    await fs.mkdir(cacheRoot,{recursive:true});
    cache = await fs.mkdtemp(path.join(cacheRoot, 'read-'));
    const file = path.join(cache, filename); await fs.writeFile(file, Buffer.concat(chunks));
    const worker = path.join(root,'src','HalconTraining.Worker','bin','Release','net8.0-windows','HalconTraining.Worker.exe');
    const output = await new Promise((resolve, reject) => execFile(worker, ['--dataset-inspect', file], {timeout:60000,maxBuffer:16*1024*1024,windowsHide:true}, (error,stdout,stderr)=>error?reject(new Error(stderr.trim()||error.message)):resolve(stdout)));
    const report = JSON.parse(output);
    for (const sample of report.samples) {
      if (!sample.missing) { const token=crypto.randomUUID(); images.set(token,sample.path); sample.imageUrl='http://127.0.0.1:8770/image/'+token; }
    }
    res.setHeader('Content-Type','application/json; charset=utf-8'); res.end(JSON.stringify(report));
  } catch (error) {
    res.writeHead(400,{'Content-Type':'application/json; charset=utf-8'}); res.end(JSON.stringify({error:error.message}));
  } finally {
    if(ownsTest)testing=false;
    if (cache) await fs.rm(cache,{recursive:true,force:true});
  }
}).listen(8770,'127.0.0.1',()=>process.stdout.write('数据集预览服务：http://127.0.0.1:8770\n'));
