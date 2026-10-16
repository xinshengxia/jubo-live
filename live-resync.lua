-- live-resync.lua —— 给 B站等直播流用的「刷新 / 追到最新」快捷键
-- 安装位置: %APPDATA%\mpv\scripts\live-resync.lua
-- 配合 input.conf 使用：
--   Ctrl+r  script-binding live_resync/reload
--   Ctrl+l  script-binding live_resync/jump_live

local mp = require "mp"
local msg = require "mp.msg"

-- 记录加载时间，用于刷新后给用户一个提示
local load_count = 0

mp.register_event("file-loaded", function()
    load_count = load_count + 1
    local fmt = mp.get_property("video-format") or "?"
    local w = mp.get_property("width") or 0
    local h = mp.get_property("height") or 0
    if load_count > 1 then
        mp.osd_message("已刷新直播流  (" .. fmt .. " " .. w .. "x" .. h .. ")", 2)
    end
end)

-- ① 重新解析并加载（最彻底：让 yt-dlp 重新取一份带新签名的流地址）
--
-- 关键：必须用 path，不能用 stream-open-filename。
-- 走 yt-dlp 播放时两者的区别（实测日志）:
--   path                  = https://live.bilibili.com/7777   ← 原始页面地址
--   stream-open-filename  = edl://!new_stream;...%841%https://d1--cn-gotcha208b.bilivideo.com/...
-- 后者是 yt-dlp 包装出来的 EDL，里面嵌的直链签名会过期；
-- 重新加载它只是复用旧地址，刷新不出新流。用 path 才会重新触发 yt-dlp 解析。
mp.add_key_binding(nil, "reload", function()
    local path = mp.get_property("path")
    if not path or path == "" then
        mp.osd_message("刷新失败：拿不到直播间地址", 3)
        return
    end
    mp.osd_message("正在刷新直播流（重新解析地址）...", 2)
    msg.info("resync reload via path: " .. tostring(path))
    mp.commandv("loadfile", path, "replace")
end)

-- ② 追到直播最新位置（不重新解析，只是跳到缓存末尾）
mp.add_key_binding(nil, "jump_live", function()
    local seekable = mp.get_property("seekable")
    if seekable ~= "yes" then
        -- 直播流通常不可 seek，这时退化成重新加载
        msg.info("stream not seekable, fallback to reload")
        mp.commandv("script-binding", "live_resync/reload")
        return
    end
    local cache_end = mp.get_property_number("demuxer-cache-time")
    if cache_end and cache_end > 0 then
        mp.commandv("seek", cache_end, "absolute", "exact")
        mp.osd_message("已跳到直播最新位置", 2)
    else
        mp.commandv("script-binding", "live_resync/reload")
    end
end)

-- ③ 自动追帧（默认关闭，用 Ctrl+Shift+l 切换）
-- 直播延迟往往来自缓冲堆积，定期跳到缓存末尾能显著降低延迟。
local auto_timer = nil
local function auto_tick()
    local cache_end = mp.get_property_number("demuxer-cache-time")
    local pos = mp.get_property_number("time-pos")
    if cache_end and pos and (cache_end - pos) > 6 then
        mp.commandv("seek", cache_end, "absolute", "exact")
        msg.info(string.format("auto resync: lag was %.1fs", cache_end - pos))
    end
end
mp.add_key_binding(nil, "auto_toggle", function()
    if auto_timer then
        auto_timer:stop()
        auto_timer = nil
        mp.osd_message("自动追直播已关闭", 2)
    else
        auto_timer = mp.add_periodic_timer(5, auto_tick)
        mp.osd_message("自动追直播已开启（延迟超 6 秒自动追上）", 3)
    end
end)

msg.info("live-resync loaded")