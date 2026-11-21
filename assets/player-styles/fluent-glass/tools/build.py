"""Build an original Fluent-style main skin using the installed skin as fallback.

Run from this project: .venv/Scripts/python.exe tools/build.py
"""
from __future__ import annotations

import argparse
import io
import json
import re
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile
import xml.etree.ElementTree as ET
import resvg_py
from secondary_skins import build_secondary

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
# ModernZ pass 2 (local patch)
# ModernZ-style overlay title bar (local patch)
SRC = ROOT / "src"
DIST = ROOT / "dist"
S = 3
INK = "#FFFFFF"
DIM = "#A2A2A2"
ACCENT = "#FF8232"
SURFACE = "#202020"


def font(size, bold=False):
    paths = ["msyhbd.ttc" if bold else "msyh.ttc", "segoeuib.ttf" if bold else "segoeui.ttf"]
    for name in paths:
        path = Path("C:/Windows/Fonts") / name
        if path.exists():
            return ImageFont.truetype(str(path), size)
    return ImageFont.load_default(size=size)


def material(w, h, kind="body"):
    """Uniform Win11 surface; native startup glass replaces this exact color."""
    return Image.new('RGBA',(w,h),SURFACE)


def title_material(w=4096, h=36):
    """ModernZ-style overlay title bar: translucent black, darkest at top."""
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    for y in range(h):
        t = y / max(1, h - 1)
        alpha = round(212 - 182 * t)
        d.line([(0, y), (w, y)], fill=(0, 0, 0, alpha))
    return im


def footer_material(w=4096, h=108):
    """ModernZ-style OSC fade: black at the bottom, fading toward the video."""
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    for y in range(h):
        t = y / max(1, h - 1)
        alpha = round(75 + 160 * t)
        d.line([(0, y), (w, y)], fill=(0, 0, 0, alpha))
    return im

def glyph(canvas, name, cx, cy, size, color, line=1.6):
    d=ImageDraw.Draw(canvas)
    names={'play':'play','pause':'pause','prev':'skip-back','next':'skip-forward','back':'rotate-ccw','forward':'rotate-cw','folder':'folder-open','volume':'volume-2','mute':'volume-x','list':'list-video','fullscreen':'maximize','min':'minus','max':'square','restore':'copy','close':'x','more':'ellipsis','mini':'picture-in-picture-2','controls':'sliders-horizontal'}
    if name in names:
        svg=(ROOT/'assets'/'lucide'/(names[name]+'.svg')).read_text().replace('currentColor',color)
        icon=Image.open(io.BytesIO(resvg_py.svg_to_bytes(svg_string=svg,width=round(size*S),height=round(size*S)))).convert('RGBA')
        canvas.alpha_composite(icon,(round((cx-size/2)*S),round((cy-size/2)*S)))
        if name in ('back','forward'):
            d.text((round(cx*S),round((cy+1)*S)),'5',font=font(8*S),fill=color,anchor='mm')
        return


class Atlas:
    def __init__(self):
        self.im=Image.new("RGBA",(1536,1536))
        self.y=256
        self.items={}
        self.hires={}

    def button(self,name,w,h,icon=None,primary=False,text=None):
        y=self.y
        self.y+=h+5
        strip=Image.new("RGBA",(w*4*S,h*S))
        d=ImageDraw.Draw(strip)
        # ModernZ-style window controls: hover tints the glyph, no pill fill.
        hover_colors={'min':'#43CB44','max':'#F8BC3A','restore':'#F8BC3A','close':'#F45C5B'}
        for state in range(4):
            off=state*w
            if primary:
                fill=['#292929','#3B3B3B','#303030','#242424'][state]
            elif name=="close":
                fill=[None,"#C42B3C","#AD2333",None][state]
            else:
                fill=[None,"#3A3A3A","#303030",None][state]
            if name in hover_colors and state==1:
                fill=None
            if fill:
                d.rounded_rectangle(((off+1)*S,1*S,(off+w-2)*S,(h-2)*S),radius=(h/2 if primary and not text else 6)*S,fill=fill)
            if primary:
                d.ellipse(((off+2)*S,2*S,(off+w-3)*S,(h-3)*S),outline=['#414141','#666666','#505050','#353535'][state],width=2*S)
            color=INK if state<3 else '#686868'
            if name in hover_colors and state==1:
                color=hover_colors[name]
            if icon:
                glyph(strip,icon,off+(25 if text else w/2),h/2,14 if name in ('min','max','restore','close') else 20,color)
            if text:
                d.text(((off+45)*S,h/2*S),text,font=font(13*S),fill=color,anchor="lm")
        self.im.alpha_composite(strip.resize((w*4,h),Image.Resampling.LANCZOS),(0,y))
        self.hires[name]=strip
        self.items[name]=(0,y,w,h)
        return name

    def attrs(self,name):
        x,y,w,h=self.items[name]
        return f"ImageLeft='{x}' ImageTop='{y}' ImageWidth='{w}' ImageHeight='{h}' Width='{w}' Height='{h}'"


def decode(b):
    return b.decode("utf-16" if b.startswith((b"\xff\xfe",b"\xfe\xff")) else "utf-8-sig")


def generate(default):
    SRC.mkdir(exist_ok=True)
    DIST.mkdir(exist_ok=True)
    (SRC/'LICENSE_LUCIDE.txt').write_bytes((ROOT/'assets'/'lucide'/'LICENSE').read_bytes())
    (SRC/'LICENSE_FLUENT_GLASS.txt').write_bytes((ROOT/'LICENSE').read_bytes())
    (SRC/'THIRD_PARTY_NOTICES.md').write_bytes((ROOT/'THIRD_PARTY_NOTICES.md').read_bytes())
    with ZipFile(default) as z:
        native_commands=set()
        # Preserve compatibility surfaces; the startup and main window are replaced.
        for n in z.namelist():
            p=SRC/n
            if not p.resolve().is_relative_to(SRC.resolve()):
                raise ValueError("Unsafe reference archive path")
            p.parent.mkdir(exist_ok=True,parents=True)
            payload=z.read(n)
            p.write_bytes(payload)
            if p.suffix.lower() in ('.txt','.xml'):
                native_commands.update(re.findall(r"(?:CmdID|State)='([^']+)'",decode(payload)))
    a=Atlas()
    top=material(1024,36,"top")
    bottom=material(1024,108,"bottom")
    a.im.alpha_composite(top,(0,0))
    a.im.alpha_composite(bottom,(0,64))
    # Color sample pixels used by PotPlayer's text renderer.
    palette=[INK,DIM,ACCENT,"#152B3D"]
    for x,c in enumerate(palette):
        ImageDraw.Draw(a.im).point((1060+x,1455),fill=c)
    # PotPlayer samples label colors from the global Main.png, even where an
    # individual widget uses another Image. Reserve four transparent edge pixels.
    global_image=Image.open(SRC/'Main.png').convert('RGBA')
    for x,c in enumerate(palette):
        ImageDraw.Draw(global_image).point((1060+x,1455),fill=c)
    global_image.save(SRC/'Main.png')
    for name in ("min","max","restore","close"):
        a.button(name,46,34,name)
    a.button("more",36,36,"more")
    a.button("play",48,48,"play",True)
    a.button("pause",48,48,"pause",True)
    for name in ("prev","next","back","forward","folder","volume","mute","list","fullscreen","mini","controls"):
        a.button(name,36,36,name)
    # Time and volume tracks plus four-state slider thumbs.
    d=ImageDraw.Draw(a.im)
    for y,c in ((190,ACCENT),(208,"#464646"),(226,"#616161")):
        d.rounded_rectangle((0,y+7,127,y+8),radius=1,fill=c)
    for i in range(4):
        d.ellipse((160+i*16,190,169+i*16,199),fill=[ACCENT,'#D3F5FF','#79BDCE','#686868'][i])
    # The logo layer is rendered at native size by PotPlayer rather than
    # stretched to the client rectangle. Oversize it so idle fullscreen has no
    # unpainted right/bottom quadrants on large monitors.
    surface=material(4096,4096)
    surface.save(SRC/"FluentSurface.png")
    title_material().save(SRC/"FluentTitle.png")
    footer_material().save(SRC/"FluentFooter.png")
    logo_size=144
    logo_svg=(ROOT/'assets'/'logo'/'glass-orbit.svg').read_text(encoding='utf-8')
    logo_frames=[]
    for state in range(4):
        svg=logo_svg
        if state==1:
            svg=svg.replace('</svg>',"<rect x='48' y='48' width='160' height='160' rx='40' fill='white' fill-opacity='.07' stroke='white' stroke-opacity='.45' stroke-width='1.5'/></svg>")
        elif state==2:
            svg=svg.replace('</svg>',"<rect x='48' y='48' width='160' height='160' rx='40' fill='#080B20' fill-opacity='.16'/></svg>")
        frame=Image.open(io.BytesIO(resvg_py.svg_to_bytes(svg_string=svg,width=logo_size*S,height=logo_size*S))).convert('RGBA')
        if state==3:
            frame.putalpha(frame.getchannel('A').point(lambda value:round(value*.5)))
        logo_frames.append(frame)
    emblem=logo_frames[0].resize((logo_size,logo_size),Image.Resampling.LANCZOS)
    emblem.save(SRC/'FluentMark.png')
    # The centered vector mark remains the native Open button.
    a.items['open']=(0,a.y,logo_size,logo_size)
    # Slider cells are contiguous. PotPlayer advances exactly ThumbWidth for
    # each state; transparent padding belongs inside each 16x16 cell.
    sliders=Image.new('RGBA',(256,96))
    for y,color in ((0,ACCENT),(16,'#464646'),(32,'#616161')):
        svg=f"<svg xmlns='http://www.w3.org/2000/svg' width='128' height='16' viewBox='0 0 128 16'><path d='M 4 8 H 124' stroke='{color}' stroke-width='4' stroke-linecap='round'/></svg>"
        track=Image.open(io.BytesIO(resvg_py.svg_to_bytes(svg_string=svg,width=128*4,height=16*4))).convert('RGBA').resize((128,16),Image.Resampling.LANCZOS)
        sliders.alpha_composite(track,(0,y))
    for state,color in enumerate((ACCENT,'#FFB27A','#D9661F','#686868')):
        svg=f"<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><circle cx='8' cy='8' r='6' fill='{color}'/></svg>"
        thumb=Image.open(io.BytesIO(resvg_py.svg_to_bytes(svg_string=svg,width=64,height=64))).convert('RGBA').resize((16,16),Image.Resampling.LANCZOS)
        sliders.alpha_composite(thumb,(16*state,64))
    sliders.save(SRC/'FluentSliders.png')
    high_atlas=a.im.resize((1536*S,1536*S),Image.Resampling.NEAREST)
    for name,strip in a.hires.items():
        high_atlas.paste(strip,(0,a.items[name][1]*S))
    for state,frame in enumerate(logo_frames):
        high_atlas.paste(frame,(logo_size*state*S,a.y*S))
    # PotPlayer treats button source cells as native-sized; ImageWidth does not
    # act as an independent SVG/viewBox scale. Supersample, then downsample once.
    high_atlas.resize((1536,1536),Image.Resampling.LANCZOS).save(SRC/'Fluent.png')
    small=emblem.resize((22,22),Image.Resampling.LANCZOS)
    small.save(SRC/"FluentMarkSmall.png")

    def button(name,cmd,position,tip,extra=''):
        return f"<Button Image='Fluent.png' {a.attrs(name)} CmdID='{cmd}' Tooltip='{tip}' {position} {extra} />"
    def label(text,pos,size=13,color=0,cmd=""):
        return f"<Label Image='Fluent.png' Text='{text}' CmdID='{cmd}' {pos} FontName='Microsoft YaHei UI' FontSize='{size}' ColorX='{1060+color}' ColorY='1455' VertAlignment='1' />"

    logo=f"""<Logo>
        <Static State='CMD_DEFAULT_LOGO' Align='Client' Image='FluentSurface.png' ImageLeft='0' ImageTop='0' ImageWidth='4096' ImageHeight='4096'>
        {button('open','ID_BASE_OPEN',"HoriCenter='1' VertCenter='1' OffsetTop='16'",'打开文件 (Ctrl+O)，也可拖入媒体文件')}
      </Static>
      <Static State='CMD_LOADING_LOGO' Align='Client' Image='FluentSurface.png' ImageLeft='0' ImageTop='0' ImageWidth='4096' ImageHeight='4096'>
        {label('正在打开…',"Width='320' Height='38' HoriCenter='1' VertCenter='1' HoriAlignment='1'",20)}
      </Static>
    </Logo>"""
    topxml=f"""<Static Image='FluentTitle.png' Align='Top' Height='36' ImageLeft='0' ImageTop='0' ImageWidth='4096' ImageHeight='36' IsTitleBar='1'>
        {label('',"Left='20' Top='0' Width='-180' Height='36'",13,0,'CMD_TITLE_MESSAGE')}
        {button('min','CMD_MIN_WINDOW',"Left='-140' Top='1'",'最小化')}
        {button('max','CMD_MAX_WINDOW',"Left='-94' Top='1'",'最大化')}
        {button('restore','CMD_RESTORE_WINDOW',"Left='-94' Top='1'",'还原')}
        {button('close','ID_APP_EXIT',"Left='-48' Top='1'",'关闭')}
      </Static>"""
    bottomxml=f"""<Static Image='FluentFooter.png' Align='Bottom' Height='108' ImageLeft='0' ImageTop='0' ImageWidth='4096' ImageHeight='108'>
        {label('',"Left='24' Top='0' Width='65' Height='28'",11,1,'CMD_CURRENTTIME_MESSAGE')}
        <Slider Image='FluentSliders.png' CmdID='CMD_TIMEBAR_CTRL' Left='98' Top='6' Width='-122' Height='16' ImageWidth='128' ImageHeight='16' ImageLeft='0' ImageTop='0' BackImageLeft='0' BackImageTop='16' AltImageLeft='0' AltImageTop='32' ThumbImageLeft='0' ThumbImageTop='64' ThumbWidth='16' ThumbHeight='16' LeftMargin='8' RightMargin='8' />
        {label('',"Left='-84' Top='0' Width='60' Height='28'",11,1,'CMD_TOTALTIME_MESSAGE')}
        {button('folder','ID_BASE_OPEN',"Left='20' Top='48'",'打开文件')}
        {button('mini','CMD_LOAD_SKIN_XML',"Left='68' Top='48'",'迷你',extra="String='AudioMini.xml'")}
        {button('controls','ID_CONTROL_BOX_WINDOW',"Left='112' Top='48'",'播放控制')}
        {button('prev','ID_PROCESS_PREV',"HoriCenter='1' Left='-112' Top='48'",'上一项')}
        {button('back','ID_PLAYBACK_5SEC_BACK',"HoriCenter='1' Left='-56' Top='48'",'后退 5 秒')}
        {button('play','CMD_MEDIA_PLAY',"HoriCenter='1' Top='42'",'播放')}
        {button('pause','CMD_MEDIA_PAUSE',"HoriCenter='1' Top='42'",'暂停')}
        {button('forward','ID_PLAYBACK_5SEC_FORW',"HoriCenter='1' Left='56' Top='48'",'前进 5 秒')}
        {button('next','ID_PROCESS_NEXT',"HoriCenter='1' Left='112' Top='48'",'下一项')}
        {button('volume','ID_AUDIO_MUTE',"Left='-240' Top='48' AutoVisible='-1'",'静音')}
        {button('mute','ID_AUDIO_MUTE',"Left='-240' Top='48' AutoVisible='1'",'取消静音')}
        <Slider Image='FluentSliders.png' CmdID='CMD_VOLUME_CTRL' Left='-197' Top='58' Width='78' Height='16' ImageWidth='128' ImageHeight='16' ImageLeft='0' ImageTop='0' BackImageLeft='0' BackImageTop='16' ThumbImageLeft='0' ThumbImageTop='64' ThumbWidth='16' ThumbHeight='16' LeftMargin='8' RightMargin='8' />
        {button('list','ID_PLAYLIST_WINDOW',"Left='-103' Top='48'",'播放列表')}
        {button('fullscreen','CMD_FULL_WINDOW',"Left='-60' Top='48'",'全屏')}
        {button('more','CMD_MAIN_MENU',"Left='-300' Top='48' VisParentMinWidth='1000'",'更多')}
      </Static>"""
    body=f"""<MainSkinArea WidthLimit='760' HeightLimit='260' Image='Fluent.png' AlphaMode='1' UseWindowFrame='0'>
    {logo}
    <Control>
      {topxml}
      {bottomxml}
      <Static Align='Client' Contain='CMD_VIDEO_WINDOW' />
    </Control>
  </MainSkinArea>"""
    # Keep each mode's own OSC and compatibility includes; replace only its main.
    for name in ('VideoSkin.xml','AudioSkin.xml'):
        original=decode((SRC/name).read_bytes())
        start=original.index("#if<SkinType==Touch>",original.index("</MainSkinAreaOscInside>"))
        end=original.index("</MainSkinArea>",start)+len("</MainSkinArea>")
        xml=original[:start]+body+original[end:]
        xml=xml.replace('\r\r\n','\n').replace('\r\n','\n').replace('\r','\n')
        xml='\n'.join(line.rstrip() for line in xml.splitlines())+'\n'
        (SRC/name).write_text(xml,encoding='utf-8-sig',newline='\n')
    secondary=build_secondary(SRC,topxml,bottomxml,logo,native_commands)
    (ROOT/"design-tokens.json").write_text(json.dumps({"name":"Fluent Glass complete","icons":"Lucide 0.468.0 SVG, resvg supersampling","material":"unified dark surface; optional native startup backdrop; opaque during drag","minWidth":760,"minHeight":404,"titleHeight":36,"footerHeight":108,"palette":{"surface":SURFACE,"text":INK,"muted":DIM,"accent":ACCENT},"sprites":a.items,"secondary":secondary},ensure_ascii=False,indent=2),encoding="utf-8")
    validate(SRC,body,native_commands)
    out=DIST/"FluentGlass.dsf"
    with ZipFile(out,"w",ZIP_DEFLATED) as z:
        for p in sorted(SRC.rglob("*")):
            if p.is_file():
                z.write(p,p.relative_to(SRC).as_posix())
    with ZipFile(out) as z:
        assert z.testzip() is None
    print(f"Built {out} ({out.stat().st_size:,} bytes)")


def validate(src,main,native_commands):
    root=ET.fromstring(main)
    for el in root.iter():
        if "Image" in el.attrib:
            assert (src/el.attrib["Image"]).is_file(), el.attrib["Image"]
    for e in root.iter():
        # All custom widgets inherit the Fluent sprite atlas unless explicitly set.
        if "ImageLeft" not in e.attrib or e.attrib.get("Image") not in (None,"Fluent.png"):
            continue
        x,y=int(e.attrib["ImageLeft"]),int(e.attrib["ImageTop"])
        w=int(e.attrib.get("ImageWidth",e.attrib.get("Width","1")))
        h=int(e.attrib.get("ImageHeight",e.attrib.get("Height","1")))
        assert x>=0 and y>=0 and x+w*(4 if e.tag=="Button" else 1)<=1536 and y+h<=1536, e.attrib
    for e in root.iter():
        cmd=e.attrib.get("CmdID","")
        assert not cmd or cmd in native_commands, f"Command not present in default skin: {cmd}"
    print("Validated main XML, image references, sprite bounds, native command IDs")


if __name__=="__main__":
    p=argparse.ArgumentParser()
    p.add_argument("--default",type=Path,help="Your locally installed PotPlayer Skins/Default.dsf")
    args=p.parse_args()
    if args.default is None:
        config=ROOT/'.runtime'/'install.json'
        try:
            settings=json.loads(config.read_text(encoding='utf-8-sig'))
            args.default=Path(settings['playerDirectory'])/'Skins'/'Default.dsf'
        except (OSError,ValueError,KeyError,TypeError):
            p.error('Run tools/install.ps1 first, or supply --default with your local Default.dsf path')
    if not args.default.is_file():p.error('Local Default.dsf not found; run tools/install.ps1 or correct --default')
    generate(args.default)
