// Compare decoded class-ID pixels, excluding PNG metadata such as timestamps.
const fs=require('node:fs/promises');
const path=require('node:path');
const zlib=require('node:zlib');
const crypto=require('node:crypto');
const assert=require('node:assert/strict');
const args=process.argv.slice(2);
const get=name=>args[args.indexOf(name)+1];
function grayscalePixels(png){
  assert(png.subarray(0,8).equals(Buffer.from([137,80,78,71,13,10,26,10])));
  const width=png.readUInt32BE(16),height=png.readUInt32BE(20);
  assert.equal(png[24],8);assert.equal(png[25],0);assert.equal(png[28],0);
  const parts=[];
  for(let offset=8;offset<png.length;){const size=png.readUInt32BE(offset),type=png.toString('ascii',offset+4,offset+8);if(type==='IDAT')parts.push(png.subarray(offset+8,offset+8+size));offset+=size+12;}
  const raw=zlib.inflateSync(Buffer.concat(parts));assert.equal(raw.length,(width+1)*height);
  const output=Buffer.alloc(width*height);
  const paeth=(a,b,c)=>{const p=a+b-c,pa=Math.abs(p-a),pb=Math.abs(p-b),pc=Math.abs(p-c);return pa<=pb&&pa<=pc?a:pb<=pc?b:c;};
  for(let y=0;y<height;y++){
    const filter=raw[y*(width+1)];assert(filter<=4);
    for(let x=0;x<width;x++){
      const i=y*width+x,left=x?output[i-1]:0,above=y?output[i-width]:0,corner=x&&y?output[i-width-1]:0;
      const prediction=filter===0?0:filter===1?left:filter===2?above:filter===3?Math.floor((left+above)/2):paeth(left,above,corner);
      output[i]=(raw[y*(width+1)+1+x]+prediction)&255;
    }
  }
  return {width,height,pixels:output};
}
async function main(){
  if(!args.includes('--evidence')||!args.includes('--results'))throw new Error('需要--evidence和--results目录。');
  const evidenceDir=get('--evidence'),resultsDir=get('--results');
  const evidence=JSON.parse(await fs.readFile(path.join(evidenceDir,'evidence.json'),'utf8'));
  const groups=new Map();
  for(const name of await fs.readdir(resultsDir)){
    const file=path.join(resultsDir,name,'result.json'),stat=await fs.stat(file).catch(()=>null);
    if(!stat||stat.mtimeMs<Date.parse(evidence.startedAt))continue;
    const result=JSON.parse(await fs.readFile(file,'utf8'));
    if(result.modelSha256!==evidence.baseline.model)continue;
    const {pixels,width,height}=grayscalePixels(await fs.readFile(path.join(resultsDir,name,'mask.png')));
    const row={directory:name,width,height,hash:crypto.createHash('sha256').update(pixels).digest('hex')};
    const group=groups.get(result.file)||[];group.push(row);groups.set(result.file,group);
  }
  const results=[...groups].map(([file,rows])=>({file,count:rows.length,distinctPixelHashes:new Set(rows.map(r=>r.hash)).size,rows}));
  const pass=results.length>=2&&results.every(r=>r.count>=2&&r.distinctPixelHashes===1);
  const report={status:pass?'PASS':'FAIL',note:'比较解码后的类别像素，不比较包含时间元数据的PNG文件字节。',results};
  await fs.writeFile(path.join(evidenceDir,'determinism.json'),JSON.stringify(report,null,2));
  console.log(JSON.stringify(results.map(({rows,...summary})=>summary)));
  assert(pass,'相同模型和原图的预测像素不一致，或样本不足。');
}
main().catch(error=>{console.error(error);process.exitCode=1;});
