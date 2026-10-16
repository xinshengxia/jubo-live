-- live-title.lua —— 四平台统一的 mpv 窗口标题与底部控制条标题
--
-- 目标格式：  <主播 - 标题>  ·  <宽x高> <fps> <编码> <码率>kbps
--
-- 启动器通过 --script-opts 传路径，正文一律放 UTF-8 文件：
--   live_title_base_file=<启动器写好的「主播 - 标题」完整文本>（四平台共用，读到就直接当基础标题）
--   live_title_bitrate_file=<启动器写的码率文本>        （码率回退：斗鱼的流名就是码率，由启动器从 Streamlink 日志里取出）
--   live_title_media_file=<直播间标题，不含主播前缀或技术信息>（底部控制条追加当前日期时间）
--
-- 码率优先级：mpv 轨道声明的 demux-bitrate（直连流有：虎牙/斗鱼/抖音 经本地中转也是直连）
--            > 启动器给的码率提示文件（斗鱼/抖音 由启动器写）
--            > 解码器实测的 video-bitrate（B站 只剩这一条；原因见 usable() 上方的注释）。
-- 使用 mpv 已解析的原生选项表，保留路径中的中文、逗号和等号。

local mp = require "mp"

local rawOpts = mp.get_property_native("options/script-opts") or {}
local function opt(name)
    local v = rawOpts[name]
    if v == nil or v == "" then return nil end
    return v
end

local baseFile = opt("live_title_base_file")
local mediaFile = opt("live_title_media_file")
local bitrateFile = opt("live_title_bitrate_file")
local stateFile = opt("live_title_state_file")   -- LIVE / OFFLINE / REPLAY / LOOP / RECORDING / UNKNOWN|ticks
local relayFile = opt("live_title_relay_file")   -- ONLINE / RECONNECTING / OFFLINE / REPLAY-UNAVAILABLE|原因
local autoReplay = opt("live_title_autoreplay") == "yes"

local function readText(path, tailBytes)
    if not path then return nil end
    local f = io.open(path, "rb")
    if not f then return nil end
    if tailBytes then
        -- 注意：seek("end") 会把读取位置留在文件末尾，小文件必须显式回到开头，否则读到空串
        local size = f:seek("end") or 0
        local start = size - tailBytes
        if start < 0 then start = 0 end
        f:seek("set", start)
    end
    local data = f:read("*a")
    f:close()
    if not data then return nil end
    data = data:gsub("^\239\187\191", "")           -- UTF-8 BOM
    data = data:gsub("^%s+", ""):gsub("%s+$", "")
    if data == "" then return nil end
    return data
end

local function stripTimestamp(s)
    if not s or s == "" then return s end
    s = s:gsub("%s+%d%d%d%d%-%d%d%-%d%d%s+%d%d:%d%d:%d%d%s*$", "")
    s = s:gsub("%s+%d%d%d%d%-%d%d%-%d%d%s+%d%d:%d%d%s*$", "")
    s = s:gsub("%s+%d%d%d%d%-%d%d%-%d%d%s*$", "")
    return s
end

local function updateMediaTitle()
    local live = readText(mediaFile) or readText(baseFile)
    if live then
        live = live:match("^[^\r\n]*")
    else
        live = stripTimestamp(mp.get_property("media-title") or "")
        if live == "" or live == "-" or live:find("live.flv", 1, true) or live:match("^https?://") then live = "直播" end
    end
    local title = live .. " " .. os.date("%Y-%m-%d %H:%M")
    if mp.get_property("media-title") ~= title then mp.set_property("force-media-title", title) end
end

local function trackBitrateKbps()
    local tracks = mp.get_property_native("track-list") or {}
    for _, t in ipairs(tracks) do
        if t.type == "video" and t["demux-bitrate"] and t["demux-bitrate"] > 0 then
            return math.floor(t["demux-bitrate"] / 1000 + 0.5)
        end
    end
    return nil
end

-- 启动器从 Streamlink 日志里读出选中的流名（斗鱼的流名就是码率，如 8033k），写进这个文件
local function hintBitrateKbps()
    local kb = readText(bitrateFile, 64)
    if not kb then return nil end
    kb = kb:match("^(%d+)")
    if kb then return tonumber(kb) end
    return nil
end

-- 「这个属性到底能不能用」的统一判断。
-- 注意 mpv 在**轨道元数据不可用**时给的是**字面量字符串 "null"**（不是 nil）：B站 走 mpv 自带的
-- ytdl_hook 时，ytdl_hook 把 yt-dlp 给的多个格式包成 edl://，轨道是它自己写的占位轨道
-- （!delay_open,media_type=video,codec=null,w=0,h=0,fps=0）—— 于是 track-list 里没有 demux-bitrate、
-- video-format 直接返回字符串 "null"、container-fps 也拿不到。这三样正好就是标题里的「帧率 编码 码率」，
-- 所以 B站 的标题只剩宽高。2026-10-16 真机实测（room 7777）：同一条流直连播是 flv + demux-bitrate=4042460，
-- 经 ytdl_hook 播就变成 multi/edl + codec="null"。
local function usable(value)
    return value ~= nil and value ~= "" and value ~= "null" and value ~= "nil"
end

local function numberOrZero(value)
    if type(value) ~= "number" or value ~= value then return 0 end
    return value
end

-- 帧率：容器声明优先；拿不到（B站 的 edl://）就用解码器/滤镜链实测的 estimated-vf-fps 兜底。
-- 实测值带小数（60.0004 / 59.9993），拼标题时仍是原来的 %.0f，显示就是 60fps。
local function fpsValue()
    local fps = numberOrZero(mp.get_property_number("container-fps"))
    if fps <= 0 then fps = numberOrZero(mp.get_property_number("estimated-vf-fps")) end
    if fps <= 0 then return nil end
    return fps
end

-- 编码：video-format 不可用时退回 video-codec —— 它是解码器给的**长描述**
-- （"H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10"），标题里要的是短名，所以按关键字映射；
-- 认不出来就取第一段，绝不把整串描述塞进标题。
local function shortCodecName(description)
    if not usable(description) then return nil end
    local lower = string.lower(description)
    if lower:find("avc", 1, true) or lower:find("h.264", 1, true) or lower:find("h264", 1, true) then return "h264" end
    if lower:find("hevc", 1, true) or lower:find("h.265", 1, true) or lower:find("h265", 1, true) then return "hevc" end
    if lower:find("av1", 1, true) then return "av1" end
    if lower:find("vp9", 1, true) then return "vp9" end
    if lower:find("vp8", 1, true) then return "vp8" end
    return (description:match("^[^/]+") or description):gsub("%s+$", "")
end

local function codecName()
    local format = mp.get_property("video-format")
    if usable(format) then return format end
    return shortCodecName(mp.get_property("video-codec"))
end

-- 码率兜底（B站 专用路径）：解码器实测的 video-bitrate，单位 bit/s。
-- ⚠️ 它是**滑动估计**，而且抖得厉害：2026-10-16 真机同一房间 96 秒实测，2 秒一采样在
-- 1.83–6.18 Mbps 之间摆（相邻两拍最大差 1.4 Mbps）—— 直播本来就是 VBR，画面一动一静差一倍很正常。
-- 所以做三层平滑（标题每 2 秒重算一次）：
--   ① 取最近 MEASURED_WINDOW 个采样的**均值**（≈60 秒窗口）—— 窗口短了会被单拍噪声带走；
--   ② 攒够 MEASURED_MIN_SAMPLES 个采样才显示 —— 躲开起播那几秒还在爬升的瞬态值；
--   ③ 取整到 100kbps，且显示值变化小于 MEASURED_STEP_KBPS 时不动 —— 滞回，防在取整边界来回抖。
-- 参数是拿上面那条真机序列**离线仿真**挑的，不是拍脑袋（判据：首个值出现后 90 秒内改写 ≤2 次、
-- 末值与 90 秒采样中位数偏差 ≤25%）。同一条序列上的对照：
--   窗口 15 + 中位数 + 300 滞回 → 改写 6 次、末值比中位数低 30%（不合格）；
--   窗口 30 + 均值   + 500 滞回 → 改写 1 次、偏差 4%（本实现）。
local measuredSamples = {}
local measuredShown = nil
local MEASURED_WINDOW = 30          -- 30 × 2 秒 = 60 秒
local MEASURED_MIN_SAMPLES = 8      -- 约 16 秒后才有值，避开起播瞬态
local MEASURED_STEP_KBPS = 500      -- 显示值的最小变化台阶

local function measuredBitrateKbps()
    local rate = mp.get_property_number("video-bitrate")
    if type(rate) == "number" and rate > 0 then
        table.insert(measuredSamples, rate)
        if #measuredSamples > MEASURED_WINDOW then table.remove(measuredSamples, 1) end
    end
    if #measuredSamples < MEASURED_MIN_SAMPLES then return nil end
    local sum = 0
    for _, value in ipairs(measuredSamples) do sum = sum + value end
    local kbps = math.floor(sum / #measuredSamples / 100000 + 0.5) * 100
    if measuredShown == nil or math.abs(kbps - measuredShown) >= MEASURED_STEP_KBPS then
        measuredShown = kbps
    end
    return measuredShown
end

local function buildTitle()
    local w = mp.get_property_number("width")
    local h = mp.get_property_number("height")
    if not w or not h or w == 0 or h == 0 then return nil end

    -- 启动器写好的「完整基础标题」（主播 - 标题）；没有就退回媒体自带的标题
    local base = readText(baseFile)
    if base then
        base = base:match("^[^\r\n]*")
    else
        local media = stripTimestamp(mp.get_property("media-title") or "")
        if media ~= "" and media ~= "-" then base = media else base = "直播" end
    end

    local parts = { string.format("%dx%d", w, h) }
    local fps = fpsValue()
    if fps then table.insert(parts, string.format("%.0ffps", fps)) end
    local codec = codecName()
    if codec then table.insert(parts, codec) end
    -- 实测码率**每轮都要采样**（不是等前两个来源都落空才采）：这样提示文件中途消失时，
    -- 窗口里已经有足够采样，不必再从零攒。
    local measured = measuredBitrateKbps()
    local kbps = trackBitrateKbps() or hintBitrateKbps() or measured
    if kbps then table.insert(parts, string.format("%dkbps", kbps)) end

    return base .. "  ·  " .. table.concat(parts, " ")
end

local last = nil          -- 基础标题（不含状态后缀）
local suffix = ""         -- 状态后缀：[已下播] / [重连中]

local function applyTitle()
    if not last then return end
    local full = last
    if suffix ~= "" then full = last .. "  " .. suffix end
    if mp.get_property("title") ~= full then mp.set_property("title", full) end
end

updateMediaTitle()
mp.register_event("file-loaded", updateMediaTitle)

local timer
timer = mp.add_periodic_timer(2, function()
    updateMediaTitle()
    local title = buildTitle()
    if title and title ~= last then
        last = title
        applyTitle()
        mp.msg.info("窗口标题已设置: " .. title)
    end
end)

-- 下播 / 重连提示：启动器负责「房间是否在播」，中转负责「连接状态」，这里只负责显示。
-- 窗口标题变成 <主播 - 标题> · ... [已下播]，屏幕上再弹一条提示，免得让人以为是自己播坏了。
local notice = nil
mp.add_periodic_timer(2, function()
    local room = readText(stateFile, 512) or ""
    local relay = readText(relayFile, 512) or ""
    local want = ""
    local text = nil
    if relay:match("^REPLAY%-UNAVAILABLE|") then
        local reason = relay:match("^REPLAY%-UNAVAILABLE|(.+)$") or "未解析到可用直链"
        want = "[重播流不可用]"
        text = "重播流取不到（" .. reason .. "）"
    elseif room:match("^OFFLINE") or relay:match("^OFFLINE") then
        -- 中转写 OFFLINE 说明它已经确认「主播没在播」（不是网络问题），所以立刻按已下播提示
        want = "[已下播]"
        text = autoReplay
            and "主播已下播，等待重新开播…（也可以直接关闭播放器）"
            or "主播已下播；这个播放器不会自动重新取流，可关闭后重新打开"
    elseif room:match("^REPLAY") then
        want = "[重播]"
        text = "当前正在播放重播"
    elseif room:match("^LOOP") then
        want = "[轮播]"
        text = "当前正在播放轮播"
    elseif room:match("^RECORDING") then
        want = "[录播]"
        text = "当前正在播放录播"
    elseif relay:match("^RECONNECTING") then
        want = "[重连中]"
        text = "连接中断，正在重连…"
    end
    if want ~= suffix then
        suffix = want
        applyTitle()
        if text then
            mp.osd_message(text, 8)
        elseif notice then
            mp.osd_message("已恢复播放", 4)
        end
        notice = text
    end
end)
