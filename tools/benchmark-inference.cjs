// Warm HTTP benchmark: time each request independently of monitoring overhead.
const fs=require('node:fs/promises');
const path=require('node:path');
const args=process.argv.slice(2);
const get=name=>args[args.indexOf(name)+1];
async function main(){
  if(!args.includes('--image')||!args.includes('--output'))throw new Error('需要--image和--output。');
  const input=get('--image'),output=get('--output'),count=Number(args.includes('--count')?get('--count'):20);
  if(!Number.isInteger(count)||count<2||count>100)throw new Error('count范围2–100。');
  const headers={Origin:'http://127.0.0.1:8768'};
  const job=await (await fetch('http://127.0.0.1:8770/training-status',{headers})).json();
  if(job.status!=='completed')throw new Error('没有已完成模型。');
  const body=await fs.readFile(input);
  const timings=[];let imageSize;
  for(let i=0;i<count+2;i++){
    const start=performance.now();
    const response=await fetch('http://127.0.0.1:8770/test',{method:'POST',headers:{...headers,'Content-Type':'application/octet-stream','X-Image-Name':encodeURIComponent(path.basename(input)),'X-Model-Id':job.modelId},body,signal:AbortSignal.timeout(180000)});
    const result=await response.json();
    const httpMs=performance.now()-start;
    if(!response.ok)throw new Error(JSON.stringify(result));
    imageSize={width:result.width,height:result.height};
    if(i>=2)timings.push({httpMs,inferenceMs:result.inferenceMs,workerTotalMs:result.totalMs});
    process.stdout.write(`BENCH ${i+1}/${count+2}\n`);
  }
  const stats=field=>{const values=timings.map(t=>t[field]).sort((a,b)=>a-b);return {min:values[0],median:values[Math.floor(values.length/2)],p95:values[Math.ceil(values.length*.95)-1],max:values.at(-1),mean:values.reduce((a,b)=>a+b,0)/values.length};};
  const report={at:new Date().toISOString(),count,warmup:2,modelDirectory:job.output,input,...imageSize,note:'HTTP耗时包括本机传输、进程启动、模型加载、预处理、推理、文件保存及JSON返回；不含浏览器绘图。单客户端串行，未在请求计时内采集资源。',httpMs:stats('httpMs'),inferenceMs:stats('inferenceMs'),workerTotalMs:stats('workerTotalMs'),timings};
  report.throughputImagesPerSecond=1000/report.httpMs.mean;
  await fs.writeFile(output,JSON.stringify(report,null,2));
  process.stdout.write(JSON.stringify({httpMs:report.httpMs,inferenceMs:report.inferenceMs,throughput:report.throughputImagesPerSecond})+'\n');
}
main().catch(error=>{process.stderr.write(error.stack+'\n');process.exitCode=1;});
