using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace Daynote
{
    public sealed class SyncClient
    {
        const string Endpoint="https://c--ff542ec0-79c6-40d2-9610-34eb964ccd22-prod.lovable.cloud/rest/v1/rpc/daynote_sync_v1";
        const string PublicKey="sb_publishable_rlWdj3LzNurnQW9c--QvDA_udvomPd1";
        readonly object gate=new object();
        HttpWebRequest active;
        public SyncResponse Exchange(string key,string device,List<SyncChange> changes)
        {
            if(changes.Count>100)throw new InvalidOperationException("一次最多同步 100 条更改。");
            byte[] body;
            using(var memory=new MemoryStream()) {
                new DataContractJsonSerializer(typeof(SyncRequest)).WriteObject(memory,new SyncRequest{Key=key,Device=device,Changes=changes});
                body=memory.ToArray();
            }
            if(body.Length>950000)throw new InvalidOperationException("本次同步内容过大，请缩短较长备注后重试。");
            var request=(HttpWebRequest)WebRequest.Create(Endpoint);
            request.Method="POST";request.ContentType="application/json";request.Accept="application/json";
            request.Headers.Add("apikey",PublicKey);request.ContentLength=body.Length;
            request.Timeout=25000;request.ReadWriteTimeout=25000;
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            lock(gate)active=request;
            try {
                using(var output=request.GetRequestStream())output.Write(body,0,body.Length);
                using(var response=(HttpWebResponse)request.GetResponse())
                using(var input=response.GetResponseStream())
                using(var memory=new MemoryStream()) {
                    byte[] buffer=new byte[8192];int count;
                    while((count=input.Read(buffer,0,buffer.Length))>0) {
                        memory.Write(buffer,0,count);
                        if(memory.Length>32*1024*1024)throw new InvalidOperationException("云端数据超过预览版支持范围。");
                    }
                    memory.Position=0;
                    var result=(SyncResponse)new DataContractJsonSerializer(typeof(SyncResponse)).ReadObject(memory);
                    SyncEngine.ValidateResponse(result);return result;
                }
            } catch(WebException error) {
                var response=error.Response as HttpWebResponse;
                int code=response==null?0:(int)response.StatusCode;
                if(response!=null)response.Dispose();
                if(code==401 || code==403)throw new InvalidOperationException("同步密钥无效或权限已取消；本机日程仍保留。");
                if(code==400)throw new InvalidOperationException("服务未接受本次请求，请检查密钥或联系维护者；本机日程仍保留。");
                throw new InvalidOperationException("暂时无法连接同步服务，已保留本机更改，稍后自动重试。");
            } finally { Array.Clear(body,0,body.Length);lock(gate)if(Object.ReferenceEquals(active,request))active=null; }
        }
        public void Cancel() { lock(gate)if(active!=null)active.Abort(); }
    }
    public static class SyncCredentials
    {
        static string PathFor(string root) { return Path.Combine(root,"sync-key.dpapi"); }
        public static string Load(string root)
        {
            string path=PathFor(root);if(!File.Exists(path))return null;
            byte[] plain=ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); } finally { Array.Clear(plain,0,plain.Length); }
        }
        public static void Save(string root,string key)
        {
            Directory.CreateDirectory(root);
            byte[] plain=Encoding.UTF8.GetBytes(key);byte[] encrypted;
            try { encrypted=ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser); }
            finally { Array.Clear(plain,0,plain.Length); }
            string path=PathFor(root),temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { file.Write(encrypted,0,encrypted.Length);file.Flush(true); }
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
            } finally { if(File.Exists(temporary))File.Delete(temporary); }
        }
        public static void Delete(string root) { string path=PathFor(root);if(File.Exists(path))File.Delete(path); }
    }
}
