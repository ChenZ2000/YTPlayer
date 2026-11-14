Java.perform(()=>{
let synthetic=false;
const C=Java.use('com.netease.cloudmusic.utils.NeteaseMusicUtils'),S=Java.use('java.lang.String');
for(const name of ['serialurl','serialdata','serialdatanew','deserialdata','deserialdatanew','deserialdata2','deserialdata2new']){
 if(!C[name])continue;
 for(const ov of C[name].overloads){const types=ov.argumentTypes.map(x=>x.className);ov.implementation=function(...args){
  let path='',body=null;for(let i=0;i<args.length;i++)if(types[i]==='java.lang.String'){const s=String(args[i]);if(s.startsWith('/')||s.startsWith('https:'))path=s.split('?')[0];else try{body=JSON.parse(s);}catch(_){}}
  if(/captcha|login|sns\//i.test(path))send({kind:'login-request',source:synthetic?'synthetic-native-vector':'official-action',path,fields:body?Object.fromEntries(Object.entries(body).map(([k,v])=>[k,typeof v])):{},publicContract:body?Object.fromEntries(Object.entries(body).filter(([k,v])=>['ctcode','countrycode','secrete','scene','type','https','remember'].includes(k))):{}});
  const result=ov.call(this,...args);
  if(name.startsWith('deserial'))for(let i=0;i<args.length;i++)if(types[i]==='[B')try{const n=ov.returnType.className==='int'?Number(result):args[i].length;if(n<=0||n>131072)continue;const s=String(S.$new(args[i],0,n,'UTF-8'));const j=JSON.parse(s);if(j.code!==undefined)send({kind:'response',code:j.code,fields:Object.keys(j),bytes:n});}catch(_){}
  return result;
 };}
}
rpc.exports.vector=()=>new Promise(resolve=>Java.perform(()=>{const body={phone:'13800000000',countrycode:'86',captcha:'123456',type:'1',https:'true',remember:'true',secureCaptcha:''};try{synthetic=true;resolve({params:String(C.serialdata('/api/w/login/cellphone',JSON.stringify(body)))});}finally{synthetic=false;}}));
send({kind:'ready'});
});
