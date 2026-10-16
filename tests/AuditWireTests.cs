using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

internal static partial class HardeningTests
{
    private static byte[] AuditBiliPacket(byte[] body, int version)
    {
        byte[] packet=new byte[16+body.Length];
        Type type=DanmakuType("BiliDanmakuClient");
        type.GetMethod("WriteBe32",ReviewStatic).Invoke(null,new object[] {packet,0,packet.Length});
        type.GetMethod("WriteBe16",ReviewStatic).Invoke(null,new object[] {packet,4,16});
        type.GetMethod("WriteBe16",ReviewStatic).Invoke(null,new object[] {packet,6,version});
        type.GetMethod("WriteBe32",ReviewStatic).Invoke(null,new object[] {packet,8,5});
        Buffer.BlockCopy(body,0,packet,16,body.Length); return packet;
    }

    private static byte[] AuditZlib(byte[] data)
    {
        using(var output=new MemoryStream())
        { output.WriteByte(0x78); output.WriteByte(0x9c); using(var zip=new DeflateStream(output,CompressionMode.Compress,true)) zip.Write(data,0,data.Length); return output.ToArray(); }
    }

    private static void TestAuditBiliBudget()
    {
        Type type=DanmakuType("BiliDanmakuClient"); object client=Activator.CreateInstance(type,new object[] {"1",Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"))});
        MethodInfo feed=type.GetMethod("Feed",ReviewInstance);
        byte[] nested=AuditBiliPacket(Encoding.UTF8.GetBytes("{}"),0);
        for(int i=0;i<5;i++) nested=AuditBiliPacket(AuditZlib(nested),2);
        bool refused=false;
        try {feed.Invoke(client,new object[] {nested});} catch(TargetInvocationException ex) {refused=ex.InnerException is InvalidDataException;}
        Check(refused,"B站拒绝五层递归压缩");
        byte[] large=AuditBiliPacket(new byte[4300000],0); byte[] bundle=Join(AuditBiliPacket(AuditZlib(large),2),AuditBiliPacket(AuditZlib(large),2));
        refused=false;
        try {feed.Invoke(client,new object[] {bundle});} catch(TargetInvocationException ex) {refused=ex.InnerException is InvalidDataException;}
        Check(refused,"B站同一消息内多个压缩包共享展开预算");
        byte[] bad=AuditBiliPacket(new byte[0],0); bad[5]=1;
        refused=false;
        try {feed.Invoke(client,new object[] {bad});} catch(TargetInvocationException ex) {refused=ex.InnerException is InvalidDataException;}
        Check(refused,"B站无效 headerLen 在数组分配前拒绝");
        feed.Invoke(client,new object[] {AuditBiliPacket(Encoding.UTF8.GetBytes("{}"),0)});
        Check(true,"拒绝异常包后正常消息仍可解析");
        type.GetMethod("Stop").Invoke(client,null);
    }

    private static void TestAuditWssLimit()
    {
        Type socketType=DanmakuType("DanmakuSocket"); MethodInfo receive=socketType.GetMethod("ReceiveMessage",ReviewStatic);
        using (RSA rsa=RSA.Create(2048))
        {
            var request=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            using (X509Certificate2 issued=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1)))
            using (X509Certificate2 cert=new X509Certificate2(issued.Export(X509ContentType.Pfx),"",X509KeyStorageFlags.UserKeySet))
            {
                foreach (bool oversize in new bool[] { false,true,false })
                {
                    var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); Exception serverFailure=null;
                    int port=((IPEndPoint)listener.LocalEndpoint).Port;
                    var server=new Thread(delegate() {
                        try {
                            using (TcpClient tcp=listener.AcceptTcpClient()) using (var tls=new SslStream(tcp.GetStream(),false))
                            {
                                tcp.ReceiveTimeout=5000; tcp.SendTimeout=5000;
                                tls.AuthenticateAsServer(cert,false,SslProtocols.Tls12,false);
                                ReadAuditHttpHeaders(tls);
                                byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n");
                                tls.Write(response,0,response.Length);
                                int total=oversize?2*1024*1024+1:2*1024*1024;
                                int offset=0;
                                while(total>0)
                                {
                                    int count=Math.Min(total,32768); total-=count;
                                    tls.WriteByte((byte)((total==0?0x80:0)|(offset==0?2:0)));
                                    if(count<126) tls.WriteByte((byte)count);
                                    else { tls.WriteByte(126); tls.WriteByte((byte)(count>>8)); tls.WriteByte((byte)count); }
                                    byte[] body=new byte[count]; for(int i=0;i<count;i++) body[i]=(byte)((offset+i)%251);
                                    tls.Write(body,0,body.Length); offset+=count;
                                }
                                tls.Flush(); Thread.Sleep(100);
                            }
                        } catch(Exception ex) { serverFailure=ex; }
                    }); server.IsBackground=true; server.Start();
                    try {
                        using (var tcp=new TcpClient("127.0.0.1",port))
                        using (var tls=new SslStream(tcp.GetStream(),false,delegate(object sender,X509Certificate c,X509Chain chain,SslPolicyErrors errors) {
                            return c!=null && c.GetCertHashString()==cert.GetCertHashString();
                        }))
                        {
                            tcp.ReceiveTimeout=5000; tcp.SendTimeout=5000;
                            tls.AuthenticateAsClient("localhost",null,SslProtocols.Tls12,false);
                            byte[] upgrade=Encoding.ASCII.GetBytes("GET /limit HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n");
                            tls.Write(upgrade,0,upgrade.Length); ReadAuditHttpHeaders(tls);
                            using (WebSocket ws=WebSocket.CreateClientWebSocket(tls,null,16384,16384,TimeSpan.FromSeconds(30),false,WebSocket.CreateClientBuffer(16384,16384)))
                            {
                                bool rejected=false; byte[] data=null;
                                try { data=(byte[])receive.Invoke(null,new object[] { ws,new byte[16384] }); }
                                catch(TargetInvocationException ex) { rejected=ex.InnerException is InvalidDataException; }
                                if(oversize) Check(rejected && ws.State==WebSocketState.Aborted,"真实 TLS/WebSocket 分片超限终止连接，不返回残片");
                                else {
                                    Check(data!=null && data.Length==2*1024*1024,"真实 WSS 边界消息完整重组");
                                    Check(data[0]==0 && data[data.Length-1]==(byte)((data.Length-1)%251),"分片内容未截断或串入下一消息");
                                }
                            }
                        }
                    } finally { listener.Stop(); server.Join(3000); if(serverFailure!=null) Console.WriteLine("WSS fixture: "+serverFailure); }
                    Check(serverFailure==null,"受控 WSS 服务器正常收尾");
                }
            }
        }
    }

    private static void ReadAuditHttpHeaders(Stream stream)
    {
        int matched=0; byte[] end={13,10,13,10}; int bytes=0;
        while(matched<4)
        { int b=stream.ReadByte(); if(b<0 || ++bytes>8192) throw new InvalidDataException("夹具 HTTP 握手不完整"); matched=b==end[matched]?matched+1:(b==13?1:0); }
    }
}
