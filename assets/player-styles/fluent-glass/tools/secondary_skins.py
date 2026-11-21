"""Build the playlist, control panel, OSC and mini player with native commands."""
from __future__ import annotations

import io
import re
from pathlib import Path
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw
import resvg_py

SURFACE='#202020'
PANEL='#1F1F1F'
TEXT='#E7E7E7'
MUTED='#A2A2A2'
ACCENT='#9FDCEB'
SELECT='#353535'
PALETTE=(TEXT,MUTED,ACCENT,SELECT,PANEL,'#B8A3EE','#545E71','#2B2B2B')


class Widgets:
    def __init__(self,src):
        self.src=src
        self.sheet=Image.new('RGBA',(4096,2048))
        self.x,self.y,self.row=0,0,0
        self.cache={}
        for i,c in enumerate(PALETTE):ImageDraw.Draw(self.sheet).point((1060+i,1455),fill=c)

    def sprite(self,w,h,icon='',tab=False,opaque=False,bordered=False):
        key=(w,h,icon,tab,opaque,bordered)
        if key in self.cache:return self.cache[key]
        if self.x+w*4>4000:self.x=0;self.y+=self.row+4;self.row=0
        x,y=self.x,self.y
        self.x+=w*4+4;self.row=max(self.row,h)
        assert y+h<1400,'Panel sprite sheet capacity exceeded'
        strip=Image.new('RGBA',(w*12,h*3),PANEL if opaque else (0,0,0,0));d=ImageDraw.Draw(strip)
        for state in range(4):
            bx=state*w*3
            fill=('#303030','#3A3A3A','#252525','#282828')[state] if bordered else (None,'#3A3A3A','#303030',None)[state]
            outline=('#666666','#8A8A8A',ACCENT,'#484848')[state] if bordered else None
            if fill:d.rounded_rectangle((bx+3,3,bx+w*3-4,h*3-4),radius=12,fill=fill,outline=outline,width=3)
            if tab and state==2:d.rounded_rectangle((bx+24,h*3-9,bx+w*3-25,h*3-4),radius=2,fill=ACCENT)
            color=TEXT if state<3 else '#697386'
            if icon:
                path=self.src.parent/'assets'/('search.svg' if icon=='search' else 'lucide/'+icon+'.svg')
                svg=path.read_text().replace('currentColor',color)
                size=18 if w<40 else 20
                im=Image.open(io.BytesIO(resvg_py.svg_to_bytes(svg_string=svg,width=size*3,height=size*3))).convert('RGBA')
                strip.alpha_composite(im,(bx+round((w-size)/2*3),round((h-size)/2*3)))
        cell=strip.resize((w*4,h),Image.Resampling.LANCZOS)
        self.sheet.paste(cell,(x,y))
        self.cache[key]=(x,y,w,h)
        return x,y,w,h

    def button(self,cmd,x,y,w=36,h=32,text='',icon='',extra='',tip=''):
        sx,sy,_,_=self.sprite(w,h,icon=icon,tab=cmd=='CMD_SELECT_PAGE',bordered=True)
        # Let PotPlayer draw all text through the same native font renderer.
        # Bitmap lettering is rescaled with the skin at 125/150% and loses its
        # hinted strokes, even if Pillow draws it sharply in the source atlas.
        label=self.label(text,0,0,w,h,extra="HoriAlignment='1'") if text else ''
        return f"<Button Image='FluentPanels.png' CmdID='{cmd}' Left='{x}' Top='{y}' Width='{w}' Height='{h}' ImageLeft='{sx}' ImageTop='{sy}' ImageWidth='{w}' ImageHeight='{h}' Tooltip='{tip or text}' {extra}>{label}</Button>"

    def label(self,text,x,y,w,h=28,cmd='',size=14,color=0,extra=''):
        return f"<Label Image='FluentPanels.png' Text='{text}' CmdID='{cmd}' Left='{x}' Top='{y}' Width='{w}' Height='{h}' ColorX='{1060+color}' ColorY='1455' FontName='Microsoft YaHei UI' FontSize='{size}' VertAlignment='1' {extra} />"

    def surface(self,align,height,children='',extra=''):
        return f"<Static Image='FluentSurface.png' Align='{align}' Height='{height}' ImageLeft='0' ImageTop='0' ImageWidth='4096' ImageHeight='{height}' {extra}>{children}</Static>"

    def slider(self,cmd,x,y,w=180,h=16,vert=False):
        return f"<Slider Image='FluentPanelSliders.png' CmdID='{cmd}' Left='{x}' Top='{y}' Width='{w}' Height='{h}' ImageLeft='{128 if vert else 0}' ImageTop='0' ImageWidth='{16 if vert else 128}' ImageHeight='{128 if vert else 16}' BackImageLeft='{144 if vert else 0}' BackImageTop='{0 if vert else 16}' ThumbImageLeft='0' ThumbImageTop='64' ThumbWidth='16' ThumbHeight='16' {'Vert=\'1\' TopMargin=\'8\' BottomMargin=\'8\'' if vert else 'LeftMargin=\'8\' RightMargin=\'8\''} />"


def build_secondary(src,topxml,bottomxml,logo,allowed):
    ui=Widgets(src)
    # All panel labels can sample the same reserved palette, regardless of mode.
    for name in ('Main.png','Fluent.png'):
        im=Image.open(src/name).convert('RGBA');d=ImageDraw.Draw(im)
        for i,c in enumerate(PALETTE):d.point((1060+i,1455),fill=c)
        im.save(src/name)
    sliders=Image.open(src/'FluentSliders.png').convert('RGBA')
    # Keep the existing horizontal cells exactly; add independent vertical cells.
    sliders=Image.new('RGBA',(256,256));sliders.paste(Image.open(src/'FluentSliders.png'),(0,0))
    for x,c in ((128,ACCENT),(144,'#464D5B')):
        raw=f"<svg xmlns='http://www.w3.org/2000/svg' width='16' height='128'><path d='M8 8V120' stroke='{c}' stroke-width='2' stroke-linecap='round'/></svg>"
        im=Image.open(io.BytesIO(resvg_py.svg_to_bytes(svg_string=raw,width=64,height=512))).convert('RGBA').resize((16,128),Image.Resampling.LANCZOS)
        sliders.alpha_composite(im,(x,0))
    # Cyan replaces the warm accent in the panel tracks and thumbs.
    p=sliders.load()
    for y in range(96):
        for x in range(128):
            r,g,b,a=p[x,y]
            if r>g+8 and r>b+15:p[x,y]=(159,220,235,a)
    sliders.save(src/'FluentPanelSliders.png')

    # Win11 settings groups: quiet neutral surfaces and small corner radii.
    # Keep these opaque so the native player never substitutes a black brush.
    cards=Image.new('RGBA',(376,1120),SURFACE)
    cd=ImageDraw.Draw(cards)
    groups=(((0,40,375,216),),((0,0,375,183),),
            ((0,70,375,221),),((0,0,375,75),(0,84,375,151),(0,168,375,273)))
    for page,rects in enumerate(groups):
        for left,top,right,bottom in rects:
            cd.rounded_rectangle((left,top+page*280,right,bottom+page*280),
                                 radius=8,fill='#272727',outline='#333333')
    for y in (46,92,138):cd.line((12,280+y,363,280+y),fill='#353535')
    for y in (120,170):cd.line((12,560+y,363,560+y),fill='#353535')
    cards.save(src/'FluentPanelCards.png')

    # Three source pieces keep both rounded ends and the border intact at any
    # playlist width; the live native search is inset inside this frame.
    search_field=Image.new('RGBA',(4128,30),SURFACE)
    sd=ImageDraw.Draw(search_field)
    sd.rounded_rectangle((0,0,31,29),radius=6,fill='#2B2B2B',outline='#666666')
    sd.rectangle((32,0,4127,29),fill='#2B2B2B')
    sd.line((32,0,4127,0),fill='#666666');sd.line((32,29,4127,29),fill='#666666')
    search_field.save(src/'FluentSearchField.png')

    # Playlist: native list, album tabs and search remain real player controls.
    colors={'SelBkgnd':3,'Frame':4,'BkgndColor1':4,'BkgndColor2':4,'CurText':2,'SelText':0,'Text':0,'PlayedText':1,'Line':4}
    attrs=[]
    for key,index in colors.items():
        stem=key if key.startswith('Bkgnd') else key+'Color'
        attrs.extend((f"{stem}X='{1060+index}'",f"{stem}Y='1455'"))
    tx,ty,tw,th=ui.sprite(128,34,tab=True,opaque=True)
    dx,dy,dw,dh=ui.sprite(16,16,icon='x')
    lx,ly,lw,lh=ui.sprite(22,24,icon='skip-back')
    rx,ry,rw,rh=ui.sprite(22,24,icon='skip-forward')
    px,py,pw,ph=ui.sprite(22,24,icon='ellipsis')
    sx,sy,sw,sh=ui.sprite(16,16,icon='search')
    playlistattrs=' '.join(attrs)+f" AlbumTabX='{tx}' AlbumTabY='{ty}' AlbumTabW='{tw}' AlbumTabH='{th}' AlbumTabBtnX='{tx}' AlbumTabBtnY='{ty}' AlbumTabBtnW='{tw}' AlbumTabBtnH='{th}' AlbumTabTextX='1061' AlbumTabTextY='1455' AlbumTabSelTextX='1060' AlbumTabSelTextY='1455' AlbumTabFontName='Microsoft YaHei UI' AlbumTabFontSize='9' AlbumTabDelX='{dx}' AlbumTabDelY='{dy}' AlbumTabDelW='{dw}' AlbumTabDelH='{dh}' AlbumTabLeftX='{lx}' AlbumTabLeftY='{ly}' AlbumTabLeftW='{lw}' AlbumTabLeftH='{lh}' AlbumTabRightX='{rx}' AlbumTabRightY='{ry}' AlbumTabRightW='{rw}' AlbumTabRightH='{rh}' AlbumTabPopupX='{px}' AlbumTabPopupY='{py}' AlbumTabPopupW='{pw}' AlbumTabPopupH='{ph}'"
    header=ui.surface('Top',48,ui.label('播放列表',18,8,180,32,size=16)+ui.button('CMD_ATTACH_PLAYLIST',-88,8,32,32,icon='copy',tip='停靠 / 分离')+ui.button('ID_PLAYLIST_WINDOW',-46,8,32,32,icon='x',tip='关闭播放列表'),"IsTitleBar='1'")
    playlistattrs+=f" SearchIconX='{sx}' SearchIconY='{sy}' SearchIconW='{sw}' SearchIconH='{sh}' SearchIconSelX='{sx+sw}' SearchIconSelY='{sy}' SearchIconSelW='{sw}' SearchIconSelH='{sh}' SearchIconRight='0' SearchBkgndColorX='1067' SearchBkgndColorY='1455' SearchSelBkgndColorX='1067' SearchSelBkgndColorY='1455'"
    search=ui.surface('Top',42,"<Static Left='18' Top='6' Width='-18' Height='30'>"
        "<Image Image='FluentSearchField.png' Left='0' Top='0' Width='8' Height='30' ImageLeft='0' ImageTop='0' ImageWidth='8' ImageHeight='30' />"
        "<Image Image='FluentSearchField.png' Left='-8' Top='0' Width='8' Height='30' ImageLeft='24' ImageTop='0' ImageWidth='8' ImageHeight='30' />"
        "<Static Image='FluentSearchField.png' Left='8' Top='0' Width='-8' Height='30' ImageLeft='32' ImageTop='0' ImageWidth='4096' ImageHeight='30' />"
        "<Layout Contain='CMD_PLAY_LIST_SEARCH' Left='2' Top='2' Width='-2' Height='26' /></Static>")
    footer=ui.surface('Bottom',56,ui.button('CMD_PLAYLIST_ADD',16,12,70,32,'添加')+ui.button('CMD_PLAYLIST_DEL',92,12,70,32,'移除')+ui.button('CMD_PLAYLIST_MENU_SORT',168,12,70,32,'排序')+ui.button('CMD_PLAYLIST_UP',-88,12,32,32,icon='skip-back',tip='上移')+ui.button('CMD_PLAYLIST_DOWN',-46,12,32,32,icon='skip-forward',tip='下移'))
    playlist=f"<PlaylistSkinArea Image='FluentPanels.png' AlphaMode='1' WidthLimit='360' HeightLimit='300' UseWindowFrame='0' LeftMargin='8' RightMargin='8' TopMargin='8' BottomMargin='8' {playlistattrs}><Control>{header}<Static Align='Client' Contain='CMD_PLAYLIST_HOLDER'>{footer}{search}<Static Align='Client' Contain='CMD_PLAY_LIST_ALBUM_TAB' /></Static></Control></PlaylistSkinArea>"

    # Four native control pages with clear spacing and full original command set.
    header=ui.surface('Top',48,ui.label('播放控制',20,8,220,32,size=16)+ui.button('ID_CONTROL_BOX_WINDOW',-46,8,32,32,icon='x',tip='关闭控制面板'),"IsTitleBar='1'")
    tabs=''.join(ui.button('CMD_SELECT_PAGE',16+96*i,6,92,34,text,extra=f"PageIndex='{i}'") for i,text in enumerate(('音频','视频','字幕','播放')))
    tabs=ui.surface('Top',48,tabs)
    pages=[]
    p=ui.button('ID_AUDIO_EQULIZER',0,0,100,32,'均衡器',extra="DownFlag='1'")+ui.button('CMD_POPUPMENU_AUDIO_EQULIZER',108,0,156,32,'均衡器预设')+ui.button('CMD_POPUPMENU_EQULIZER_PRESET',272,0,104,32,'管理预设')
    freqs=('60','170','310','600','1k','3k','6k','12k','14k','16k')
    for i,f in enumerate(freqs):
        p+=ui.label(f,2+i*27,48,24,22,size=9,color=1,extra="HoriAlignment='1'")
        p+=ui.slider('CMD_EQULIZER_'+str(i),6+i*27,78,16,128,True)
    for x,name,cmd in ((306,'主音量','CMD_MASTER_VOLUME'),(345,'波形','CMD_WAVE_VOLUME')):
        p+=ui.label(name,x-10,48,40,22,size=9,color=1,extra="HoriAlignment='1'")+ui.slider(cmd,x,78,16,128,True)
    p+=ui.label('选择预设，或拖动各频段调节声音。',0,230,376,28,size=11,color=1)
    pages.append(p)
    p=''
    for i,(name,cmd) in enumerate((('亮度','CMD_COLOR_OFFSET'),('对比度','CMD_COLOR_GAIN'),('饱和度','CMD_COLOR_SAT'),('色相','CMD_COLOR_HUE'))):
        y=i*46+7
        p+=ui.label(name,12,y,60,32,extra="HoriAlignment='1'")+ui.slider(cmd,86,y+8,204)+ui.button(cmd+'_DEFAULT',302,y,62,32,'重置')
    for i,(name,cmd) in enumerate((('柔化','ID_VIDEO_BLUR'),('锐化','ID_VIDEO_SHARPEN'),('去块','ID_VIDEO_POSTPROC'))):
        p+=ui.button(cmd,i*126,194,118,32,name,extra="DownFlag='1'")
    p+=ui.button('ID_VIDEO_IMAGE_CAPTURE_PROMPT',0,238,376,32,'保存当前画面')
    pages.append(p)
    p=ui.button('ID_CAPTION_FONT_SELECT',0,0,376,32,'选择字幕字体')
    p+=ui.label('',0,40,376,26,'CMD_CAPTION_FONT_MESSAGE',11,1)
    p+=ui.label('字号',12,79,52,32,extra="HoriAlignment='1'")+ui.button('ID_CAPTION_DEC_SIZE',74,79,76,32,'减小')+ui.label('',158,79,60,32,'CMD_CAPTION_SIZE_MESSAGE',14,2,extra="HoriAlignment='1'")+ui.button('ID_CAPTION_INC_SIZE',226,79,76,32,'增大')
    p+=ui.label('位置',12,129,52,32,extra="HoriAlignment='1'")
    for i,(name,cmd) in enumerate((('左','ID_CAPTION_MOVE_LEFT'),('上','ID_CAPTION_MOVE_UP'),('下','ID_CAPTION_MOVE_DOWN'),('右','ID_CAPTION_MOVE_RIGHT'))):p+=ui.button(cmd,74+i*76,129,70,32,name)
    p+=ui.label('同步',12,179,52,32,extra="HoriAlignment='1'")+ui.button('ID_CAPTION_SLOW_SYNC',74,179,100,32,'延后')+ui.button('ID_CAPTION_FAST_SYNC',182,179,100,32,'提前')+ui.button('ID_CAPTION_RESET_SYNC',290,179,86,32,'重置')
    p+=ui.label('调整会应用于当前字幕。',0,234,376,28,size=11,color=1)
    pages.append(p)
    p=ui.label('跳转',12,0,352,28,size=14,color=1)
    for i,(name,cmd,alt) in enumerate((('-1 分钟','ID_PLAYBACK_1MIN_BACK','ID_PLAYBACK_5MIN_BACK'),('-5 秒','ID_PLAYBACK_5SEC_BACK','ID_PLAYBACK_30SEC_BACK'),('+5 秒','ID_PLAYBACK_5SEC_FORW','ID_PLAYBACK_30SEC_FORW'),('+1 分钟','ID_PLAYBACK_1MIN_FORW','ID_PLAYBACK_5MIN_FORW'))):p+=ui.button(cmd,i*96,34,88,32,name,extra=f"AltCmdID='{alt}'")
    p+=ui.label('播放速度',12,84,352,26,size=14,color=1)
    for i,(name,cmd) in enumerate((('减速','ID_PLAY_SPEED_SLOWER'),('正常速度','ID_PLAY_SPEED_NORMAL'),('加速','ID_PLAY_SPEED_FASTER'))):p+=ui.button(cmd,i*128,116,120,32,name)
    p+=ui.label('AB 循环',12,168,352,26,size=14,color=1)
    p+=ui.label('',12,200,104,28,'CMD_REPEAT_START_MESSAGE',11,2,extra="HoriAlignment='1'")+ui.label('',148,200,104,28,'CMD_REPEAT_END_MESSAGE',11,2,extra="HoriAlignment='1'")
    for i,(name,cmd) in enumerate((('设为 A 点','ID_PLAY_REPEAT_START_SET'),('设为 B 点','ID_PLAY_REPEAT_STOP_SET'),('取消循环','ID_PLAY_REPEAT_RESET'))):p+=ui.button(cmd,i*128,238,120,32,name)
    pages.append(p)
    pagetext=''.join(f"<Static Image='FluentPanelCards.png' State='CMD_PAGE_CONTROL' PageIndex='{i}' Left='20' Top='56' Width='376' Height='280' ImageLeft='0' ImageTop='{i*280}' ImageWidth='376' ImageHeight='280'>{p}</Static>" for i,p in enumerate(pages))
    client=f"<Static Align='Client' Image='FluentSurface.png' ImageLeft='0' ImageTop='0' ImageWidth='416' ImageHeight='356'>{tabs}{pagetext}</Static>"
    controls=f"<ControlBoxSkinArea Image='FluentPanels.png' AlphaMode='1' Width='416' Height='404' UseWindowFrame='0'><Control>{header}{client}</Control></ControlBoxSkinArea>"
    for name,xml in (('FluentPlaylist.xml',playlist),('FluentControls.xml',controls)):
        ET.fromstring(xml)
        for cmd in re.findall(r"(?:CmdID|AltCmdID)='([^']+)'",xml):
            assert not cmd or cmd in allowed,cmd
        (src/name).write_text(xml,encoding='utf-8-sig',newline='\n')
    sub=(src/'SubCommon.txt').read_text(encoding='utf-8-sig')
    sub=re.sub(r'<ControlBoxSkinArea\b.*?</ControlBoxSkinArea>', '#include<FluentControls.xml>',sub,flags=re.S)
    sub=re.sub(r'<PlaylistSkinArea\b.*?</PlaylistSkinArea>', '#include<FluentPlaylist.xml>',sub,flags=re.S)
    (src/'SubCommon.txt').write_text(sub,encoding='utf-8-sig',newline='\n')
    ui.sheet.save(src/'FluentPanels.png')

    # Mini and control are icon buttons in the main footer itself.
    extra=''
    osc=f"<MainSkinAreaOscInside Image='Fluent.png'>{logo}<Control>{topxml}{bottomxml.replace('</Static>',extra+'</Static>')}</Control></MainSkinAreaOscInside>"
    frame=f"<MainSkinAreaOscFrame Image='Fluent.png' AlphaMode='1' WidthLimit='760' HeightLimit='0' UseWindowFrame='0'>{logo}<Control><Static Align='Client' Contain='CMD_VIDEO_WINDOW' /></Control></MainSkinAreaOscFrame>"
    (src/'FluentOsc.xml').write_text(frame+osc,encoding='utf-8-sig',newline='\n')
    for name in ('VideoSkin.xml','AudioSkin.xml'):
        text=(src/name).read_text(encoding='utf-8-sig')
        a=text.index('#if<SkinType==Touch>',text.index('#include<Common.txt>'))
        b=text.index('</MainSkinAreaOscInside>',a)+len('</MainSkinAreaOscInside>')
        text=text[:a]+'#include<FluentOsc.xml>\n'+text[b:]
        # Add direct entries for control panel and mini mode to normal footer.
        footer=text.index("Align='Bottom'",text.index('<MainSkinArea '))
        end=text.index('</Static>',footer)
        text=text[:end]+extra+text[end:]
        (src/name).write_text(text,encoding='utf-8-sig',newline='\n')

    miniheader=ui.surface('Top',36,ui.label('音频迷你',16,4,160,28,size=12,color=1)+ui.button('CMD_LOAD_SKIN_XML',-158,2,100,32,'标准模式',extra="String='AudioSkin.xml'")+ui.button('CMD_MIN_WINDOW',-88,2,32,32,icon='minus',tip='最小化')+ui.button('ID_APP_EXIT',-44,2,32,32,icon='x',tip='关闭'),"IsTitleBar='1'")
    # Restore button sits left of min/close, with sufficient separation.
    miniheader=miniheader.replace("Left='-158'","Left='-200'")
    Image.open(src/'FluentMark.png').resize((64,64),Image.Resampling.LANCZOS).save(src/'FluentMiniMark.png')
    card=ui.surface('Top',76,"<Image Image='FluentMiniMark.png' Left='18' Top='4' Width='64' Height='64' ImageLeft='0' ImageTop='0' ImageWidth='64' ImageHeight='64' />"+ui.label('',94,8,-116,30,'CMD_TITLE_WITH_MESSAGE',13)+ui.label('',94,40,120,24,'CMD_CURRENTTIME_MESSAGE',11,2)+ui.label('',224,40,120,24,'CMD_TOTALTIME_MESSAGE',11,1))
    minifooter=ui.surface('Bottom',76,ui.slider('CMD_TIMEBAR_CTRL',18,0,-36)+ui.button('ID_BASE_OPEN',16,30,36,36,icon='folder-open',tip='打开文件')+ui.button('ID_PROCESS_PREV',-34,30,36,36,icon='skip-back',extra="HoriCenter='1'",tip='上一项')+ui.button('CMD_MEDIA_PLAY',10,30,36,36,icon='play',extra="HoriCenter='1'",tip='播放')+ui.button('CMD_MEDIA_PAUSE',10,30,36,36,icon='pause',extra="HoriCenter='1'",tip='暂停')+ui.button('ID_PROCESS_NEXT',54,30,36,36,icon='skip-forward',extra="HoriCenter='1'",tip='下一项')+ui.button('ID_AUDIO_MUTE',-144,30,32,36,icon='volume-2',tip='静音',extra="AutoVisible='-1'")+ui.button('ID_AUDIO_MUTE',-144,30,32,36,icon='volume-x',tip='取消静音',extra="AutoVisible='1'")+ui.slider('CMD_VOLUME_CTRL',-104,40,84))
    main=f"<MainSkinArea Image='FluentPanels.png' AlphaMode='1' WidthLimit='520' HeightLimit='1' ForceMinWidth='1' ForceMinHeight='1' UseWindowFrame='0'><Control>{miniheader}{card}{minifooter}<Static Align='Client' Contain='CMD_VIDEO_WINDOW' /></Control></MainSkinArea>"
    miniosc="<MainSkinAreaOscFrame Image='FluentPanels.png' AlphaMode='1' WidthLimit='520' UseWindowFrame='0'><Control><Static Align='Client' Contain='CMD_VIDEO_WINDOW' /></Control></MainSkinAreaOscFrame>\n<MainSkinAreaOscInside Image='FluentPanels.png'><Control>"+miniheader+card+minifooter+'</Control></MainSkinAreaOscInside>'
    (src/'AudioMini.xml').write_text("<?xml version='1.0'?>\n<Skin Version='1.0' MinorVersion='1000'>\n#include<Common.txt>\n"+miniosc+'\n'+main+"\n#include<SubCommon.txt #CONTROL1=Dummy #CONTROL2=Dummy>\n</Skin>\n",encoding='utf-8-sig',newline='\n')
    ui.sheet.save(src/'FluentPanels.png')
    # Child video windows use the same chromes instead of the old default skin.
    child="<?xml version='1.0'?><Skin Version='1.0' MinorVersion='1000'>#include<Common.txt><MainSkinArea Image='Fluent.png' AlphaMode='1' WidthLimit='760' UseWindowFrame='0'>"+logo+'<Control>'+topxml+bottomxml+"<Static Align='Client' Contain='CMD_VIDEO_WINDOW' /></Control></MainSkinArea>#include<SubCommon.txt #CONTROL1=Button #CONTROL2=Layout></Skin>"
    (src/'ChildSkin.xml').write_text(child,encoding='utf-8-sig',newline='\n')
    theme_scrollbars(src)
    theme_popups(src)
    for name in ('Common.txt','Logo.txt','SubCommon.txt'):
        path=src/name
        text=path.read_text(encoding='utf-8-sig')
        path.write_text('\n'.join(line.rstrip() for line in text.splitlines())+'\n',encoding='utf-8-sig',newline='\n')
    return {'playlist':'FluentPlaylist.xml','controls':'FluentControls.xml','fullscreen':'FluentOsc.xml','audioMini':'AudioMini.xml'}


def theme_scrollbars(src):
    # Preserve native scrollbar geometry, replace only the old bitmap palette.
    for name in ('ScrollBar.bmp','ScrollBarPL.bmp','ScrollBarCH.bmp','ScrollBarCH_Dark.bmp'):
        path=src/name
        if not path.exists():continue
        im=Image.open(path).convert('RGB')
        source=im.load();out=Image.new('RGB',im.size);pixels=out.load()
        for y in range(im.height):
            for x in range(im.width):
                r,g,b=source[x,y];value=(r+g+b)/3
                if value<40:color=(32,34,41)
                elif value<90:color=(46,52,64)
                elif value<160:color=(77,88,106)
                else:color=(149,173,195)
                pixels[x,y]=color
        out.save(path)


def theme_popups(src):
    # Menu definitions sample the global Main image; reserve a new unused band.
    base=Image.open(src/'Main.png').convert('RGBA')
    im=Image.new('RGBA',(base.width,2256));im.paste(base,(0,0))
    d=ImageDraw.Draw(im)
    colors=(PANEL,TEXT,MUTED,SELECT,ACCENT,'#3C4556')
    for i,c in enumerate(colors):d.rectangle((1000,2000+i*8,1007,2007+i*8),fill=c)
    d.rounded_rectangle((800,2000,855,2023),radius=4,fill=PANEL,outline='#485163',width=1)
    check="<svg xmlns='http://www.w3.org/2000/svg' width='14' height='14'><path d='m2 7 3 3 7-7' fill='none' stroke='#9FDCEB' stroke-width='1.6' stroke-linecap='round' stroke-linejoin='round'/></svg>"
    mark=Image.open(io.BytesIO(resvg_py.svg_to_bytes(svg_string=check,width=56,height=56))).convert('RGBA').resize((14,14),Image.Resampling.LANCZOS)
    im.alpha_composite(mark,(860,2000))
    raw="<svg xmlns='http://www.w3.org/2000/svg' width='12' height='12'><circle cx='6' cy='6' r='3' fill='#9FDCEB'/></svg>"
    dot=Image.open(io.BytesIO(resvg_py.svg_to_bytes(svg_string=raw,width=48,height=48))).convert('RGBA').resize((12,12),Image.Resampling.LANCZOS)
    im.alpha_composite(dot,(880,2000));im.save(src/'Main.png')
    attrs="FrameX='800' FrameY='2000' FrameW='56' FrameH='24' SelTextColorX='1000' SelTextColorY='2008' TextColorX='1000' TextColorY='2008' SelColorX='1000' SelColorY='2024' DimTextColorX='1000' DimTextColorY='2016' LineColorX='1000' LineColorY='2040' CheckX='860' CheckY='2000' CheckW='14' CheckH='14' RadioX='880' RadioY='2000' RadioW='12' RadioH='12' ScrollbarIndex='1'"
    definitions=f"<CustomPopupMenuDef {attrs} />\n<CustomPopupMenuAlt {attrs} />\n"
    for name in ('Common.txt','Logo.txt'):
        text=(src/name).read_text(encoding='utf-8-sig')
        text=re.sub(r'<CustomPopupMenu(?:Def|Alt)\b[^>]*/>','',text)
        text+='\n'+definitions
        (src/name).write_text(text,encoding='utf-8-sig',newline='\n')
