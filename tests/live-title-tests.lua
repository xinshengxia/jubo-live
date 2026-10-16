local real_mp = require "mp"
local options = real_mp.get_property_native("options/script-opts") or {}
local source = assert(options.title_test_source)
local directory = assert(options.title_test_dir)
local real_date = os.date
local checks = 0
local function check(value, message)
    if not value then error(message) end
    checks = checks + 1
end
local function write(path, text)
    local file = assert(io.open(path, "wb"))
    file:write(text)
    file:close()
end
local ok, failure = pcall(function()
    for _, platform in ipairs({"bili", "huya", "douyu", "douyin"}) do
        local room_title = platform .. " 直播标题 - 中文，逗号,等号=测试"
        local full_title = "主播 - 艺名 - " .. room_title
        local prefix = directory .. "/" .. platform
        local base = prefix .. "-base,中文.txt"
        local media = prefix .. "-media,中文.txt"
        local bitrate = prefix .. "-bitrate,中文.txt"
        local state = prefix .. "-state,中文.txt"
        local relay = prefix .. "-relay,中文.txt"
        write(base, full_title); write(media, room_title); write(bitrate, "3000"); write(state, ""); write(relay, "")
        local properties = {
            ["media-title"] = platform == "bili" and (room_title .. " 1999-01-01 00:00") or "live.flv?t=synthetic-token",
            ["video-format"] = "h264", width = 640, height = 360, ["container-fps"] = 25
        }
        local script_options = {live_title_base_file = base, live_title_media_file = media,
            live_title_bitrate_file = bitrate, live_title_state_file = state, live_title_relay_file = relay}
        local timers, events = {}, {}
        local osd = nil
        local clock = "2030-01-01 23:59"
        os.date = function() return clock end
        local fake = {
            get_property = function(name) return name == "options/script-opts" and "legacy=broken" or properties[name] end,
            get_property_number = function(name) return properties[name] end,
            get_property_native = function(name) return name == "options/script-opts" and script_options or {} end,
            set_property = function(name, value)
                properties[name] = value
                if name == "force-media-title" then properties["media-title"] = value end
            end,
            add_periodic_timer = function(_, callback) timers[#timers + 1] = callback; return {} end,
            register_event = function(name, callback) events[name] = callback end,
            osd_message = function(text) osd = text end,
            msg = {info = function() end}
        }
        package.loaded.mp = fake
        dofile(source)
        timers[1]()
        check(properties["force-media-title"] == room_title .. " " .. clock, platform .. " 控制条应为直播标题和当前日期时间")
        check(properties.title == full_title .. "  ·  640x360 25fps h264 3000kbps", platform .. " 顶部窗口标题和技术信息应保持原样")
        clock = "2030-01-02 00:00"; timers[1]()
        check(properties["media-title"] == room_title .. " " .. clock, platform .. " 时间跨分钟/日期后应更新")
        check(not properties["media-title"]:find("synthetic-token", 1, true), platform .. " 控制条不应显示中转令牌")
        write(state, "OFFLINE"); timers[2]()
        check(properties.title:find("[已下播]", 1, true) ~= nil, platform .. " 保留顶部下播状态")
        check(properties["media-title"] == room_title .. " " .. clock, platform .. " 下播状态不覆盖控制条标题")
        local replayState = platform == "bili" and "LOOP" or platform == "douyu" and "RECORDING" or "REPLAY"
        local replayLabel = platform == "bili" and "[轮播]" or platform == "douyu" and "[录播]" or "[重播]"
        write(state, replayState); timers[2]()
        check(properties.title:find(replayLabel, 1, true) ~= nil, platform .. " 顶部区分重播/轮播/录播")
        write(relay, "REPLAY-UNAVAILABLE|页面没有可用的 FLV 直链"); timers[2]()
        check(properties.title:find("[重播流不可用]", 1, true) ~= nil, platform .. " 顶部显示重播流不可用")
        check(osd == "重播流取不到（页面没有可用的 FLV 直链）", platform .. " OSD 给出准确重播失败原因")
        properties["media-title"] = "live.flv?t=reloaded"
        check(events["file-loaded"] ~= nil, platform .. " 重新加载应刷新控制条")
        events["file-loaded"]()
        check(properties["media-title"] == room_title .. " " .. clock, platform .. " 重新加载后不回退到文件名")
    end

    -- 追加场景：B站 经 mpv 自带 ytdl_hook 播放时的真实形状（2026-10-16 真机 dump）。
    -- ytdl_hook 把流包成 edl:// 占位轨道，于是 track-list 没有 demux-bitrate、video-format 是
    -- **字面量字符串** "null"、container-fps 拿不到；这时必须回退到 video-codec / estimated-vf-fps /
    -- video-bitrate，否则 B站 标题会塌回「<主播 - 标题>  ·  1920x1080」。
    -- 这是本次修复的回归用例：前四轮覆盖不到这条路径（它们的 video-format 是 h264、且有码率提示文件）。
    local room_title = "bili 直播标题 - 中文，逗号,等号=测试"
    local full_title = "主播 - 艺名 - " .. room_title
    local prefix = directory .. "/bili-edl"
    local base = prefix .. "-base,中文.txt"
    local media = prefix .. "-media,中文.txt"
    write(base, full_title); write(media, room_title)
    -- 注意：**不给** live_title_bitrate_file —— B站 这条路启动器本来就不写码率提示文件
    local properties = {
        ["media-title"] = room_title .. " 1999-01-01 00:00",
        ["video-format"] = "null",                                     -- mpv 的字面量 "null"
        ["video-codec"] = "H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10",
        ["estimated-vf-fps"] = 60.0004,
        ["video-bitrate"] = 3220000,                                   -- bit/s → 取整后 3200kbps
        width = 1920, height = 1080
    }
    local script_options = {live_title_base_file = base, live_title_media_file = media,
        live_title_state_file = prefix .. "-state,中文.txt", live_title_relay_file = prefix .. "-relay,中文.txt"}
    local timers, events = {}, {}
    os.date = function() return "2030-01-01 23:59" end
    package.loaded.mp = {
        get_property = function(name) return name == "options/script-opts" and "legacy=broken" or properties[name] end,
        get_property_number = function(name) return properties[name] end,
        get_property_native = function(name) return name == "options/script-opts" and script_options or {} end,
        set_property = function(name, value)
            properties[name] = value
            if name == "force-media-title" then properties["media-title"] = value end
        end,
        add_periodic_timer = function(_, callback) timers[#timers + 1] = callback; return {} end,
        register_event = function(name, callback) events[name] = callback end,
        osd_message = function() end,
        msg = {info = function() end}
    }
    dofile(source)
    -- 实测码率要攒够 8 个采样才显示（躲开起播那几秒的爬升），所以这里连算八轮（8 × 2 秒 = 16 秒）
    for _ = 1, 8 do timers[1]() end
    check(properties.title == full_title .. "  ·  1920x1080 60fps h264 3200kbps",
        "B站(edl://) 缺 demux-bitrate 时应回退到 video-codec / estimated-vf-fps / video-bitrate")
    check(properties.title:find("null", 1, true) == nil, "B站(edl://) 标题里不应出现字面量 null")
end)
package.loaded.mp = real_mp
os.date = real_date
if ok then
    real_mp.msg.info("PASS: 四平台控制条/时钟/窗口标题/下播/重载，共 " .. checks .. " 条断言")
    real_mp.commandv("quit", 0)
else
    real_mp.msg.error("FAIL: " .. tostring(failure))
    real_mp.commandv("quit", 1)
end
