package app.youmu.daynote;

import android.content.Context;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.AtomicFile;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.util.Arrays;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/** Only ciphertext is persisted, in Android's no-backup directory. */
public final class SecureKeyStore {
    private final AtomicFile file;
    private final String alias;
    public SecureKeyStore(Context context) {
        File directory=new File(context.getNoBackupFilesDir(),"daynote-native");
        if(!directory.isDirectory()&&!directory.mkdirs())throw new IllegalStateException("无法创建密钥目录。");
        file=new AtomicFile(new File(directory,"sync-key.bin"));
        alias=context.getPackageName()+".sync.aes.v1";
    }
    private SecretKey key(boolean create) throws Exception {
        KeyStore store=KeyStore.getInstance("AndroidKeyStore"); store.load(null);
        if(store.containsAlias(alias))return (SecretKey)store.getKey(alias,null);
        if(!create)throw new IllegalStateException("系统密钥已不可用，请重新输入原同步码。");
        KeyGenerator generator=KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(alias,KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256).setRandomizedEncryptionRequired(true).build());
        return generator.generateKey();
    }
    public String load() throws Exception {
        if(!file.getBaseFile().exists()&&!new File(file.getBaseFile()+".bak").exists())return null;
        byte[] bytes=file.readFully();
        if(bytes.length>4096)throw new IllegalStateException("保存的密钥格式无效，请重新连接。");
        try(DataInputStream input=new DataInputStream(new ByteArrayInputStream(bytes))) {
            if(input.readInt()!=1)throw new IllegalStateException("保存的密钥版本无效。");
            int length=input.readInt(); if(length!=12)throw new IllegalStateException("保存的密钥格式无效。");
            byte[] iv=new byte[length]; input.readFully(iv);
            byte[] encrypted=new byte[input.available()]; input.readFully(encrypted);
            Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE,key(false),new GCMParameterSpec(128,iv));
            byte[] plain=cipher.doFinal(encrypted);
            try { return new String(plain,StandardCharsets.UTF_8); } finally { Arrays.fill(plain,(byte)0); }
        } finally { Arrays.fill(bytes,(byte)0); }
    }
    public void save(String value) throws Exception {
        byte[] plain=value.getBytes(StandardCharsets.UTF_8);
        byte[] bytes;
        try {
            Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding"); cipher.init(Cipher.ENCRYPT_MODE,key(true));
            byte[] iv=cipher.getIV(),encrypted=cipher.doFinal(plain);
            ByteArrayOutputStream buffer=new ByteArrayOutputStream();
            try(DataOutputStream output=new DataOutputStream(buffer)) { output.writeInt(1); output.writeInt(iv.length); output.write(iv); output.write(encrypted); }
            bytes=buffer.toByteArray();
        } finally { Arrays.fill(plain,(byte)0); }
        FileOutputStream output=null;
        try { output=file.startWrite(); output.write(bytes); file.finishWrite(output); }
        catch(Exception error) { if(output!=null)file.failWrite(output); throw error; }
        finally { Arrays.fill(bytes,(byte)0); }
    }
    public void clear() {
        file.delete();
        if(file.getBaseFile().exists())throw new IllegalStateException("无法移除本机同步码。");
    }
}
