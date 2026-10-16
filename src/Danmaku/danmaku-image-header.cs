using System;

// 在 GDI+/WIC/VP8L 分配像素前读取容器尺寸。只接受已识别的远程图片格式。
internal static class DanmakuImageHeader
{
    private static int Le16(byte[] b, int p) { return b[p] | b[p+1] << 8; }
    private static uint Le32(byte[] b, int p) { return (uint)(b[p] | b[p+1] << 8 | b[p+2] << 16 | b[p+3] << 24); }
    private static uint Be32(byte[] b, int p) { return (uint)(b[p] << 24 | b[p+1] << 16 | b[p+2] << 8 | b[p+3]); }
    private static int Le24(byte[] b, int p) { return b[p] | b[p+1] << 8 | b[p+2] << 16; }
    private static bool Is(byte[] b, int p, string s)
    { if (p < 0 || p > b.Length-s.Length) return false; for (int i=0;i<s.Length;i++) if (b[p+i] != s[i]) return false; return true; }
    private static bool Fits(long w, long h)
    { return w > 0 && h > 0 && w <= DanmakuImageCache.MaxDimension && h <= DanmakuImageCache.MaxDimension; }

    internal static bool IsWithinLimit(byte[] b)
    {
        if (b == null || b.Length < 10 || b.Length > DanmakuImageCache.MaxBytes) return false;
        if (b.Length >= 24 && b[0] == 137 && Is(b,1,"PNG\r\n\x1a\n") && Is(b,12,"IHDR"))
            return Fits(Be32(b,16),Be32(b,20));
        if (Is(b,0,"GIF87a") || Is(b,0,"GIF89a")) return CheckGif(b);
        if (b.Length >= 26 && Is(b,0,"BM"))
        {
            uint dib = Le32(b,14);
            if (dib == 12) return Fits(Le16(b,18),Le16(b,20));
            if (dib >= 40 && b.Length >= 54) return Fits((int)Le32(b,18),Math.Abs((long)(int)Le32(b,22)));
            return false;
        }
        if (Is(b,0,"RIFF") && Is(b,8,"WEBP"))
        {
            long end = 8L + Le32(b,4);
            if (end > b.Length || end < 12) return false;
            long budget=0; int frames=0; bool sized=false;
            return CheckWebp(b,12,end,0,ref budget,ref frames,ref sized) && sized;
        }
        if (b[0]==0xff && b[1]==0xd8)
        {
            int p=2;
            while (p < b.Length)
            {
                if (b[p++]!=0xff) return false;
                while (p<b.Length && b[p]==0xff) p++;
                if (p>=b.Length) return false; int marker=b[p++];
                if (marker==0xd9 || marker==0xda) return false;
                if (marker==1 || (marker>=0xd0 && marker<=0xd8)) continue;
                if (p+2>b.Length) return false; int len=b[p]<<8 | b[p+1];
                if (len<2 || len>b.Length-p) return false;
                if (marker>=0xc0 && marker<=0xcf && marker!=0xc4 && marker!=0xc8 && marker!=0xcc)
                    return len>=8 && Fits(b[p+5]<<8 | b[p+6],b[p+3]<<8 | b[p+4]);
                p+=len;
            }
        }
        return false;
    }

    private static bool CheckWebp(byte[] b,long start,long end,int depth,ref long budget,ref int frames,ref bool sized)
    {
        long canvasBytes=0;
        for(long cursor=start;cursor<end;)
        {
            if(cursor+8>end) return false;
            int p=(int)cursor,body=p+8; uint length=Le32(b,p+4);
            if(length>end-body) return false;
            if(Is(b,p,"VP8X"))
            { if(depth!=0 || length<10 || !Fits(1+Le24(b,body+4),1+Le24(b,body+7))) return false; canvasBytes=(long)(1+Le24(b,body+4))*(1+Le24(b,body+7))*4; sized=true; }
            else if(Is(b,p,"VP8L"))
            {
                if(length<5 || b[body]!=0x2f) return false;
                uint bits=Le32(b,body+1); long w=1+(bits&0x3fff),h=1+((bits>>14)&0x3fff);
                if(!Fits(w,h)) return false; sized=true; budget+=w*h*4;
            }
            else if(Is(b,p,"VP8 "))
            {
                if(length<10 || b[body+3]!=0x9d || b[body+4]!=1 || b[body+5]!=0x2a) return false;
                int w=Le16(b,body+6)&0x3fff,h=Le16(b,body+8)&0x3fff;
                if(!Fits(w,h)) return false; sized=true; budget+=(long)w*h*4;
            }
            else if(Is(b,p,"ANMF"))
            {
                if(depth!=0 || canvasBytes==0 || length<16 || ++frames>256) return false;
                long w=1+Le24(b,body+6),h=1+Le24(b,body+9);
                if(!Fits(w,h)) return false;
                // 帧与解码器的输出画布都记预算；嵌套 VP8/VP8L 尺寸也必须复核。
                budget+=Math.Max(w*h*4,canvasBytes);
                if(!CheckWebp(b,body+16,body+(long)length,depth+1,ref budget,ref frames,ref sized)) return false;
            }
            if(budget>DanmakuImageCache.MaxCacheBytes) return false;
            cursor=body+(long)length+(length&1);
            if(cursor>end) return false;
        }
        return true;
    }

    private static bool CheckGif(byte[] b)
    {
        if(b.Length<13) return false;
        int w=Le16(b,6),h=Le16(b,8); if(!Fits(w,h)) return false;
        int p=13,frames=0;
        if((b[10]&128)!=0) p+=3*(1<<((b[10]&7)+1));
        while(p<b.Length)
        {
            int marker=b[p++];
            if(marker==0x3b) return frames>0;
            if(marker==0x21) {if(p>=b.Length) return false;p++;}
            else if(marker==0x2c)
            {
                if(p+9>b.Length || ++frames>256 || (long)frames*w*h*4>DanmakuImageCache.MaxCacheBytes) return false;
                if(!Fits(Le16(b,p+4),Le16(b,p+6))) return false;
                int packed=b[p+8]; p+=9;
                if((packed&128)!=0) p+=3*(1<<((packed&7)+1));
                if(p>=b.Length) return false;p++; // LZW 最小码长。
            }
            else return false;
            while(true)
            {
                if(p>=b.Length) return false;int n=b[p++];if(n==0) break;
                if(n>b.Length-p) return false;p+=n;
            }
        }
        return false;
    }
}
