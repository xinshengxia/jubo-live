using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

internal static partial class HardeningTests
{
    private static int RunAuditFlvCapture(string input, string output)
    {
        try
        {
            InitializeReviewTestPaths();
            var original=new List<byte[]>();
            using (var stream=File.OpenRead(input))
            { stream.Position=13; byte[] tag; while ((tag=(byte[])Call("ReadCompleteFlvTag",stream,null))!=null) if(tag[0]!=18) original.Add(tag); }
            var segments=new List<byte[]>();
            for(int segment=0;segment<4;segment++)
            {
                var tags=new List<byte[]>(); tags.Add(FlvHeader);
                foreach(byte[] tag in original)
                {
                    int ts=(int)Call("FlvTagTimestamp",tag); bool header=(bool)Call("FlvTagIsCodecHeader",tag);
                    if(segment==2 && ts<6000 && !header) continue;
                    byte[] copy=(byte[])tag.Clone(); if(segment==0) Call("SetFlvTagTimestamp",copy,ts+6000L);
                    tags.Add(copy);
                }
                if(segment==2)
                    foreach(byte[] tag in original)
                    {
                        int ts=(int)Call("FlvTagTimestamp",tag); if(ts<7000 || (bool)Call("FlvTagIsCodecHeader",tag)) continue;
                        byte[] copy=(byte[])tag.Clone(); Call("SetFlvTagTimestamp",copy,ts+1000L); tags.Add(copy);
                    }
                segments.Add(Join(tags.ToArray()));
            }
            var upstream=new TcpListener(IPAddress.Loopback,0); upstream.Start();
            var downstream=new TcpListener(IPAddress.Loopback,0); downstream.Start();
            int port=((IPEndPoint)upstream.LocalEndpoint).Port;
            using (var done=new ManualResetEvent(false))
            {
                Exception serverFailure=null,pumpFailure=null;
                var server=new Thread(delegate() {
                    try { foreach(byte[] segment in segments)
                        using(TcpClient tcp=upstream.AcceptTcpClient()) using(NetworkStream s=tcp.GetStream())
                        { ReadRequest(s); byte[] h=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: "+segment.Length+"\r\nConnection: close\r\n\r\n"); s.Write(h,0,h.Length); s.Write(segment,0,segment.Length); }
                    } catch(Exception ex) {serverFailure=ex;} finally { done.Set(); upstream.Stop(); }
                }); server.IsBackground=true; server.Start();
                Thread pump=null;
                try
                {
                    using(var player=new TcpClient("127.0.0.1",((IPEndPoint)downstream.LocalEndpoint).Port))
                    using(var client=downstream.AcceptTcpClient())
                    {
                        Delegate resolver=RelayResolverFor(new FakeResolver {Port=port});
                        pump=new Thread(delegate() { try { Call("Pump",client,"douyu","encoded-fixture",resolver); } catch(Exception ex) {pumpFailure=ex;} }); pump.IsBackground=true; pump.Start();
                        NetworkStream s=player.GetStream(); s.ReadTimeout=1500;
                        byte[] request=Encoding.ASCII.GetBytes("GET /live.flv HTTP/1.1\r\nHost: localhost\r\n\r\n"); s.Write(request,0,request.Length); ReadRequest(s);
                        using(var file=File.Create(output))
                        { byte[] bytes=new byte[16384]; while(true) { int n; try {n=s.Read(bytes,0,bytes.Length);} catch(IOException) {if(done.WaitOne(0)) break; throw;} if(n<=0) break; file.Write(bytes,0,n); } }
                        player.Close();
                    }
                }
                finally { upstream.Stop(); downstream.Stop(); server.Join(3000); if(pump!=null) pump.Join(4000); }
                if(serverFailure!=null) throw serverFailure; if(pumpFailure!=null) throw pumpFailure;
            }
            long[] last={-1,-1}; int audio=0,video=0;
            using(var stream=File.OpenRead(output))
            {
                stream.Position=13; byte[] tag;
                while((tag=(byte[])Call("ReadCompleteFlvTag",stream,null))!=null)
                {
                    if((bool)Call("FlvTagIsCodecHeader",tag)) continue;
                    int slot=tag[0]==8?0:tag[0]==9?1:-1; if(slot<0) continue;
                    int ts=(int)Call("FlvTagTimestamp",tag);
                    if(ts<last[slot]) throw new InvalidDataException("真实编码帧时间线倒退"); last[slot]=ts;
                    if(slot==0) audio++; else video++;
                }
            }
            if(audio<900 || video<230 || Math.Min(last[0],last[1])<30000) throw new InvalidDataException("重连后的音视频帧丢失");
            Console.WriteLine("audioTags="+audio+" videoTags="+video+" audioEndMs="+last[0]+" videoEndMs="+last[1]);
            return 0;
        }
        catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
    }
}
