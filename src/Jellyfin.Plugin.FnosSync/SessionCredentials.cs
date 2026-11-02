using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.FnosSync;

// Runtime files only. Never ship the credential directory or key in a release.
public sealed class SessionCredentials
{
    readonly object gate=new();
    readonly string directory;
    string scope="", password="";
    long revision;
    public string? StorageError { get; private set; }
    public long Revision { get { lock(gate)return revision; } }
    string KeyPath=>Path.Combine(directory,"credential.key");
    string RecordPath=>Path.Combine(directory,"credential.json");
    public sealed class Envelope
    {
        public int Version { get; set; } = 1;
        public string Scope { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Ciphertext { get; set; } = "";
        public string Tag { get; set; } = "";
    }
    public SessionCredentials(string stateDirectory)
    {
        directory=Path.Combine(stateDirectory,"credentials");
        try
        {
            if(!File.Exists(RecordPath))return;
            var record=JsonSerializer.Deserialize<Envelope>(File.ReadAllText(RecordPath))??throw new InvalidDataException();
            if(record.Version!=1)throw new InvalidDataException();
            var key=File.ReadAllBytes(KeyPath);
            var plain=new byte[Convert.FromBase64String(record.Ciphertext).Length];
            try
            {
                using var aes=new AesGcm(key,16);
                aes.Decrypt(Convert.FromBase64String(record.Nonce),Convert.FromBase64String(record.Ciphertext),Convert.FromBase64String(record.Tag),plain,Encoding.UTF8.GetBytes(record.Scope));
                password=Encoding.UTF8.GetString(plain);scope=record.Scope;
            }
            finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
            ProtectDirectory();ProtectFile(KeyPath);ProtectFile(RecordPath);
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or FormatException or ArgumentException)
        {
            scope="";password="";
            StorageError="已保存的飞牛凭据无法读取，请重新填写密码并保存；迁移时须同时保留凭据文件和密钥。";
        }
    }
    public bool Has(string accountScope) { lock(gate)return scope==accountScope&&password.Length>0; }
    public string Get(string accountScope)
    {
        lock(gate)
        {
            if(scope!=accountScope||password.Length==0)throw new InvalidOperationException(StorageError??"尚未保存飞牛密码，请在插件设置中填写并保存。");
            return password;
        }
    }
    public void Update(string accountScope,string? suppliedPassword)
    {
        lock(gate)
        {
            if(!string.IsNullOrEmpty(suppliedPassword))
            {
                Persist(accountScope,suppliedPassword);
                scope=accountScope;password=suppliedPassword;StorageError=null;revision++;
            }
            else if(scope!=accountScope)
            {
                // A new service/user must never inherit another account's secret.
                if(File.Exists(RecordPath))File.Delete(RecordPath);
                scope=accountScope;password="";StorageError=null;revision++;
            }
        }
    }
    public void Clear()
    {
        lock(gate){if(File.Exists(RecordPath))File.Delete(RecordPath);password="";scope="";StorageError=null;revision++;}
    }
    void Persist(string accountScope,string value)
    {
        ProtectDirectory();
        if(!File.Exists(KeyPath))
        {
            var generated=RandomNumberGenerator.GetBytes(32);
            try{WritePrivate(KeyPath,generated,false);}
            finally{CryptographicOperations.ZeroMemory(generated);}
        }
        var key=File.ReadAllBytes(KeyPath);var plain=Encoding.UTF8.GetBytes(value);
        try
        {
            var nonce=RandomNumberGenerator.GetBytes(12);var tag=new byte[16];var cipher=new byte[plain.Length];
            using var aes=new AesGcm(key,16);
            aes.Encrypt(nonce,plain,cipher,tag,Encoding.UTF8.GetBytes(accountScope));
            var record=new Envelope{Scope=accountScope,Nonce=Convert.ToBase64String(nonce),Ciphertext=Convert.ToBase64String(cipher),Tag=Convert.ToBase64String(tag)};
            WritePrivate(RecordPath,JsonSerializer.SerializeToUtf8Bytes(record),true);
        }
        finally{CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
    }
    void ProtectDirectory()
    {
        if(OperatingSystem.IsWindows())Directory.CreateDirectory(directory);
        else
        {
            Directory.CreateDirectory(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        }
    }
    static void ProtectFile(string path)
    {
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    }
    static void WritePrivate(string path,byte[] bytes,bool overwrite)
    {
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,Options=FileOptions.WriteThrough};
            if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            using(var file=new FileStream(temp,options)){file.Write(bytes);file.Flush(true);}
            File.Move(temp,path,overwrite);ProtectFile(path);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
