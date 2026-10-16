// Real HALCON lifecycle/stability validation. All generated data stays under artifacts/.
// Usage: node tools/validate-lifecycle.cjs --dataset <hdict> --model-dir <run> --image <png> --image <png>
const fs=require('node:fs/promises');
const path=require('node:path');
const crypto=require('node:crypto');
const assert=require('node:assert/strict');
const {spawn,execFile}=require('node:child_process');
const {promisify}=require('node:util');
const exec=promisify(execFile);
const root=path.resolve(__dirname,'..');
const args=process.argv.slice(2);
const option=(name,fallback)=>{const i=args.indexOf(name);return i<0?fallback:args[i+1];};
const dataset=option('--dataset');
const modelDir=option('--model-dir');
const images=args.flatMap((arg,i)=>arg==='--image'?[args[i+1]]:[]);
if(!dataset||!modelDir||images.length<2)throw new Error('需要--dataset、--model-dir和至少两个--image。');
const repeats=Number(option('--repeats','20'));
if(!Number.isInteger(repeats)||repeats<2||repeats>100)throw new Error('重复次数范围2–100。');
const base='http://127.0.0.1:8770';
const origin='http://127.0.0.1:8768';
const worker=path.join(root,'src/HalconTraining.Worker/bin/Release/net8.0-windows/HalconTraining.Worker.exe');
const pretrained=path.join(root,'assets/pretrained/pretrained_dl_segmentation_compact.hdl');
const output=path.join(root,'artifacts','lifecycle',new Date().toISOString().replace(/[:.]/g,'-'));
const evidence={startedAt:new Date().toISOString(),scope:'真实语义分割；短轮次训练；短时稳定性，非长期浸泡测试',checks:[],workers:[],performance:{},output};
const hash=async file=>crypto.createHash('sha256').update(await fs.readFile(file)).digest('hex').toUpperCase();
async function save(){await fs.writeFile(path.join(output,'evidence.json'),JSON.stringify(evidence,null,2));}
async function check(name,action){const start=performance.now();try{const detail=await action();evidence.checks.push({name,status:'PASS',ms:performance.now()-start,detail});process.stdout.write('PASS '+name+'\n');}catch(error){evidence.checks.push({name,status:'FAIL',ms:performance.now()-start,error:error.message});process.stdout.write('FAIL '+name+': '+error.message+'\n');}await save();}
async function gpu(){try{return (await exec('nvidia-smi',['--query-gpu=name,memory.used,utilization.gpu','--format=csv,noheader,nounits'],{windowsHide:true})).stdout.trim();}catch{return '不可用';}}
async function processStats(){try{return JSON.parse((await exec('powershell.exe',['-NoProfile','-Command',"$p = Get-NetTCPConnection -LocalPort 8770 -State Listen -ErrorAction Stop; Get-Process -Id $p.OwningProcess | Select-Object Id,WorkingSet64,PrivateMemorySize64,HandleCount,CPU | ConvertTo-Json -Compress"],{windowsHide:true})).stdout);}catch{return null;}}
async function remainingWorkers(){try{return JSON.parse((await exec('powershell.exe',['-NoProfile','-Command',"@(Get-CimInstance Win32_Process -Filter \"Name = 'HalconTraining.Worker.exe'\" | Where-Object { $_.ExecutablePath -like '*HalconTrainingPlatform*' } | Select-Object ProcessId,CommandLine) | ConvertTo-Json -Compress"],{windowsHide:true})).stdout||'[]');}catch{return null;}}
function runWorker(name,arguments_,{timeout=240000,stopAfterMs}={}){return new Promise((resolve,reject)=>{
  const started=performance.now(),proc=spawn(worker,arguments_,{windowsHide:true,cwd:root});
  const record={name,pid:proc.pid,arguments:arguments_,startedAt:new Date().toISOString()};evidence.workers.push(record);
  let stdout='',stderr='',timedOut=false,forced=false,peakRss=0;
  proc.stdout.on('data',chunk=>stdout+=chunk);proc.stderr.on('data',chunk=>stderr+=chunk);
  const timer=setTimeout(()=>{timedOut=true;proc.kill();},timeout);
  const stop=stopAfterMs?setTimeout(()=>{forced=true;proc.kill();},stopAfterMs):null;
  let sampling=false;
  const sample=setInterval(async()=>{if(sampling)return;sampling=true;try{const value=(await exec('powershell.exe',['-NoProfile','-Command',`(Get-Process -Id ${proc.pid} -ErrorAction SilentlyContinue).WorkingSet64`],{windowsHide:true})).stdout.trim();peakRss=Math.max(peakRss,Number(value)||0);}catch{}finally{sampling=false;}},2000);
  proc.once('error',error=>{clearTimeout(timer);clearTimeout(stop);clearInterval(sample);reject(error);});
  proc.once('close',async(code,signal)=>{clearTimeout(timer);clearTimeout(stop);clearInterval(sample);Object.assign(record,{code,signal,forced,timedOut,ms:performance.now()-started,peakRss});await fs.writeFile(path.join(output,name+'.stdout.log'),stdout);await fs.writeFile(path.join(output,name+'.stderr.log'),stderr);resolve({...record,stdout,stderr});});
});}
async function request(route,{body,headers={},method='GET'}={}){const response=await fetch(base+route,{method,body,headers:{Origin:origin,...headers},signal:AbortSignal.timeout(240000)});const raw=await response.text();let value;try{value=JSON.parse(raw);}catch{value=raw;}return {status:response.status,value};}
async function imageTest(image,modelId){return request('/test',{method:'POST',headers:{'Content-Type':'application/octet-stream','X-Image-Name':encodeURIComponent(path.basename(image)),'X-Model-Id':modelId},body:await fs.readFile(image)});}
const pngSize=buffer=>({width:buffer.readUInt32BE(16),height:buffer.readUInt32BE(20)});
async function main(){
await fs.mkdir(output,{recursive:true});process.stdout.write('EVIDENCE '+output+'\n');
const baseline={dataset:await hash(dataset),model:await hash(path.join(modelDir,'model.hdl')),pretrained:await hash(pretrained)};
evidence.baseline={...baseline,gpu:await gpu(),server:await processStats(),workers:await remainingWorkers()};
let inspection,trained=false;
await check('归档文件完整性',async()=>{const sums=JSON.parse(await fs.readFile(path.join(modelDir,'checksums.json'),'utf8'));for(const [file,sum] of Object.entries(sums))assert.equal(await hash(path.join(modelDir,file)),sum);return Object.keys(sums);});
await check('字典识别与重复划分确定性',async()=>{const first=await runWorker('inspect-1',['--dataset-inspect',dataset]);assert.equal(first.code,0,first.stderr);inspection=JSON.parse(first.stdout);assert.equal(inspection.task,'semantic');const second=await runWorker('inspect-2',['--dataset-inspect',dataset]);assert.equal(second.code,0,second.stderr);assert.deepEqual(JSON.parse(second.stdout),inspection);return {images:inspection.imageCount,classes:inspection.classCount,split:inspection.splitCounts,issues:inspection.issues};});
const trainDir=path.join(output,'short-training');
await check('真实1轮训练与独立保存',async()=>{const run=await runWorker('training',['--semantic-train',dataset,'--model',pretrained,'--output',trainDir,'--epochs','1','--batch','2','--width','512','--height','512','--rate','0.001']);assert.equal(run.code,0,run.stderr);const events=run.stdout.trim().split(/\r?\n/).map(JSON.parse);assert(events.some(e=>e.stage==='训练完成'));const trainEvents=events.filter(e=>e.stage==='训练');assert(trainEvents.length>0&&trainEvents.every(e=>Number.isFinite(e.detail.loss)));for(const name of ['model.hdl','preprocess.hdict','split.json','manifest.json'])assert((await fs.stat(path.join(trainDir,name))).size>0);trained=true;return {epochs:1,batches:trainEvents.length,loss:trainEvents.at(-1).detail.loss,ms:run.ms,peakRss:run.peakRss,model:await hash(path.join(trainDir,'model.hdl'))};});
if(trained){
  await check('训练划分覆盖与互斥',async()=>{const split=JSON.parse(await fs.readFile(path.join(trainDir,'split.json'),'utf8'));const all=[...split.train,...split.validation,...split.test];assert.equal(all.length,inspection.imageCount);assert.equal(new Set(all).size,all.length);assert(all.every(i=>i>=0&&i<inspection.imageCount));return {train:split.train.length,validation:split.validation.length,test:split.test.length};});
  await check('预览前100张划分映射抽查（非全量一致性）',async()=>{const split=JSON.parse(await fs.readFile(path.join(trainDir,'split.json'),'utf8'));for(let i=0;i<inspection.samples.length;i++){const expected=split.train.includes(i)?'训练集':split.validation.includes(i)?'验证集':'测试集';assert.equal(inspection.samples[i].split,expected,'原始索引'+i+'划分不一致');}return {checked:inspection.samples.length,total:inspection.imageCount,note:'按原始样本索引抽查，接口仅返回前100张。'};});
  await check('新训练模型独立进程重载与评估',async()=>{const run=await runWorker('evaluation',['--semantic-evaluate',dataset,'--output',trainDir]);assert.equal(run.code,0,run.stderr);const report=JSON.parse(run.stdout);assert.equal(report.status,'reload_and_evaluation_passed');assert(Number.isFinite(report.reports.test.meanIoU));return report;});
  await check('新训练模型原图推理',async()=>{const run=await runWorker('new-model-test',['--semantic-test',images[0],'--model-dir',trainDir,'--output',path.join(output,'new-model-test')]);assert.equal(run.code,0,run.stderr);return JSON.parse(run.stdout);});
}
await check('Worker异常退出与重新运行',async()=>{const broken=path.join(output,'invalid.png');await fs.writeFile(broken,'invalid image');const bad=await runWorker('invalid-image',['--semantic-test',broken,'--model-dir',modelDir,'--output',path.join(output,'invalid-result')]);assert.notEqual(bad.code,0);assert(bad.stderr.length>0);const good=await runWorker('after-invalid',['--semantic-test',images[0],'--model-dir',modelDir,'--output',path.join(output,'after-invalid')]);assert.equal(good.code,0,good.stderr);return {badExit:bad.code,recovered:good.code};});
await check('强制终止Worker后回收与再推理',async()=>{const stopped=await runWorker('forced-stop',['--semantic-test',images[0],'--model-dir',modelDir,'--output',path.join(output,'forced-stop')],{stopAfterMs:500});assert(stopped.forced);assert.notEqual(stopped.code,0);const next=await runWorker('after-stop',['--semantic-test',images[1],'--model-dir',modelDir,'--output',path.join(output,'after-stop')]);assert.equal(next.code,0,next.stderr);return {forcedPid:stopped.pid,recovered:next.code,note:'强制终止，不代表界面协作取消已实现'};});
let modelId,firstResult;
await check('本地服务发现既有模型',async()=>{const response=await request('/training-status');assert.equal(response.status,200);assert.equal(response.value.status,'completed');assert.equal(path.resolve(response.value.output),path.resolve(modelDir));modelId=response.value.modelId;return {status:response.value.status,modelId};});
if(modelId){
  await check('两张原图独立推理及持久化',async()=>{const results=[];for(const image of images){const response=await imageTest(image,modelId);assert.equal(response.status,200,JSON.stringify(response.value));const r=response.value;for(const file of ['original.png','mask.png'])assert.deepEqual(pngSize(await fs.readFile(path.join(r.outputDirectory,file))),{width:r.width,height:r.height});assert(Math.abs(r.areas.reduce((n,a)=>n+a.ratio,0)-1)<1e-6);results.push({file:r.file,ms:r.inferenceMs,width:r.width,height:r.height});firstResult??=r;}return results;});
  await check('并发请求互斥与忙状态',async()=>{const results=await Promise.all([imageTest(images[0],modelId),imageTest(images[1],modelId)]);assert.deepEqual(results.map(r=>r.status).sort(),[200,409]);return results.map(r=>({status:r.status,error:r.value.error}));});
  await check('连续推理稳定性与性能',async()=>{const runs=[],memory=[];const start=performance.now();memory.push(await processStats());for(let i=0;i<repeats;i++){const r=await imageTest(images[i%images.length],modelId);assert.equal(r.status,200,JSON.stringify(r.value));runs.push({file:r.value.file,inferenceMs:r.value.inferenceMs,totalMs:r.value.totalMs});if((i+1)%5===0)memory.push(await processStats());process.stdout.write(`INFERENCE ${i+1}/${repeats}\n`);}const wallMs=performance.now()-start;const values=runs.map(r=>r.inferenceMs).sort((a,b)=>a-b);const httpWallPerImageMs=wallMs/repeats;evidence.performance={repeats,failures:0,wallMs,httpWallPerImageMs,throughputImagesPerSecond:1000/httpWallPerImageMs,inferenceMedianMs:values[Math.floor(values.length/2)],inferenceP95Ms:values[Math.ceil(values.length*.95)-1],memory,runs,gpuAfter:await gpu()};return evidence.performance;});
  await check('HTTP坏图混入后正常请求恢复',async()=>{for(let i=0;i<3;i++){const bad=await request('/test',{method:'POST',headers:{'Content-Type':'application/octet-stream','X-Image-Name':'invalid.png','X-Model-Id':modelId},body:Buffer.from('invalid')});assert.equal(bad.status,400);const good=await imageTest(images[i%images.length],modelId);assert.equal(good.status,200);}return {failuresExpected:3,recoveries:3};});
  await check('无效模型及越权来源拒绝',async()=>{const invalid=await imageTest(images[0],'unknown');assert.equal(invalid.status,400);const denied=await fetch(base+'/training-status',{headers:{Origin:'http://invalid.example'}});assert.equal(denied.status,403);return {invalidModel:400,invalidOrigin:403};});
}
await check('源字典与基础及既有模型未改写',async()=>{assert.equal(await hash(dataset),baseline.dataset);assert.equal(await hash(pretrained),baseline.pretrained);assert.equal(await hash(path.join(modelDir,'model.hdl')),baseline.model);return baseline;});
await check('临时输入与Worker退出回收',async()=>{for(const name of ['dataset-preview','test-input']){const entries=await fs.readdir(path.join(root,'artifacts',name)).catch(()=>[]);assert.equal(entries.length,0,name+'临时目录未回收');}const remaining=await remainingWorkers();assert(remaining!==null,'无法查询Worker进程');assert.equal(Array.isArray(remaining)?remaining.length:remaining?1:0,0);return {workers:remaining,gpu:await gpu(),server:await processStats()};});
for(const [name,detail] of [
  ['界面开始/停止训练','训练页按钮仍disabled，未连接作业提交和取消。'],
  ['应用启动恢复与断点续训','无已接通的训练作业持久化/中断恢复流程。'],
  ['完整模型包自动归档','CLI仅写model、preprocess、split、manifest；classes、参数、校验和与日志完整封装尚未自动完成。'],
  ['WPF完整训练测试生命周期','当前实测使用HTML界面与Worker；WPF训练和测试入口未同步。'],
  ['其他任务生命周期','此次仅验证语义分割，不覆盖目标检测、分类等任务。'],
  ['长期浸泡稳定性','此次为短时循环，不代表数小时/数天持续运行。']
])evidence.checks.push({name,status:'NOT_IMPLEMENTED_OR_NOT_TESTED',detail});
evidence.endedAt=new Date().toISOString();evidence.summary=evidence.checks.reduce((a,c)=>(a[c.status]=(a[c.status]||0)+1,a),{});await save();process.stdout.write(JSON.stringify(evidence.summary)+'\n');
process.exitCode=evidence.checks.some(c=>c.status==='FAIL')?1:0;
}
main().catch(async error=>{process.stderr.write(error.stack+'\n');evidence.fatal=error.message;await save().catch(()=>{});process.exitCode=1;});
