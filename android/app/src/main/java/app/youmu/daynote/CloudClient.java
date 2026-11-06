package app.youmu.daynote;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.List;
import javax.net.ssl.HttpsURLConnection;

/** The publishable API key is public. Private device-pairing keys are never logged. */
public final class CloudClient {
    private static final String ENDPOINT="https://c--ff542ec0-79c6-40d2-9610-34eb964ccd22-prod.lovable.cloud/rest/v1/rpc/daynote_sync_v1";
    private static final String PUBLIC_KEY="sb_publishable_rlWdj3LzNurnQW9c--QvDA_udvomPd1";
    public SyncCore.Response sync(String key,String device,List<SyncCore.Mutation> changes) throws Exception {
        JSONArray mutations=new JSONArray(); for(SyncCore.Mutation change:changes)mutations.put(change.toJson());
        byte[] body=new JSONObject().put("p_key",key).put("p_device",device).put("p_changes",mutations).toString().getBytes(StandardCharsets.UTF_8);
        if(changes.size()>100||body.length>950000)throw new IOException("待同步内容过大，请分批重试。");
        HttpsURLConnection connection=(HttpsURLConnection)new URL(ENDPOINT).openConnection();
        connection.setRequestMethod("POST"); connection.setConnectTimeout(20000); connection.setReadTimeout(25000);
        connection.setInstanceFollowRedirects(false); connection.setDoOutput(true); connection.setUseCaches(false);
        connection.setRequestProperty("Content-Type","application/json; charset=utf-8");
        connection.setRequestProperty("Accept","application/json"); connection.setRequestProperty("apikey",PUBLIC_KEY);
        connection.setFixedLengthStreamingMode(body.length);
        try {
            try(OutputStream output=connection.getOutputStream()) { output.write(body); }
            int code=connection.getResponseCode();
            if(code==401||code==403)throw new IOException("同步码无效或已停用，本机日程仍保留。");
            if(code==400)throw new IOException("云端未接受日程格式，本机更改已保留。请检查最近编辑的事项。");
            if(code<200||code>=300)throw new IOException("暂时无法连接云端，本机更改已保留，稍后重试。");
            try(InputStream input=connection.getInputStream(); ByteArrayOutputStream output=new ByteArrayOutputStream()) {
                byte[] buffer=new byte[8192]; int read;
                while((read=input.read(buffer))!=-1) { if(output.size()+read>32*1024*1024)throw new IOException("云端日程超过读取上限，本机日程仍保留。"); output.write(buffer,0,read); }
                try { return SyncCore.Response.fromJson(new JSONObject(output.toString(StandardCharsets.UTF_8.name()))); }
                catch(Exception error) { throw new IOException("云端返回的数据不完整，本机日程仍保留。"); }
            }
        } finally { Arrays.fill(body,(byte)0); connection.disconnect(); }
    }
}
