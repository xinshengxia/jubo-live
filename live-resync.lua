-- Explicitly loaded by the launcher, also usable as ordinary mpv key bindings.
local mp = require "mp"
local options = mp.get_property_native("options/script-opts") or {}

-- Recovery is enabled only for a launcher's per-session state file. Only the
-- first playlist entry and its own replacements belong to this subscription.
local function install_recovery(resolve_source)
    local enabled = type(options.live_title_state_file) == "string" and options.live_title_state_file ~= ""
    local origin, next_path, generation = nil, nil, 0
    local busy, stopped, failed = false, false, false
    local delays = {2, 4, 8, 15, 30, 60}
    local attempts, due, previous_pos, stable = 0, nil, nil, 0
    local original_idle = mp.get_property("idle")
    local owns_idle = false

    local function restore_idle()
        if owns_idle then
            owns_idle = false
            mp.set_property("idle", original_idle or "no")
        end
    end

    local function cancel()
        generation = generation + 1
        busy, stopped, failed = false, true, false
        due, next_path = nil, nil
        restore_idle()
    end

    local function hint(path)
        if type(path) ~= "string" or path == "" then return "" end
        local file = io.open(path, "rb")
        if not file then return "" end
        local value = file:read(512) or ""
        file:close()
        return value:gsub("^\239\187\191", ""):match("^%s*([A-Z_-]+)") or ""
    end

    local function blocked()
        local room = hint(options.live_title_state_file)
        local relay = hint(options.live_title_relay_file)
        return room == "OFFLINE" or relay == "OFFLINE" or relay == "REPLAY-UNAVAILABLE"
            or relay == "VERIFICATION"
    end

    local function reload(manual)
        local current = mp.get_property("path")
        local path = current or (enabled and not stopped and origin)
        if not path or path == "" or busy then return end
        if not manual and (stopped or blocked() or mp.get_property_native("pause")) then return end
        if manual then
            attempts, due = 0, nil
            mp.osd_message("正在刷新直播流…", 2)
        else
            attempts = math.min(attempts + 1, #delays)
            mp.osd_message(attempts == #delays and "连接仍未恢复，每 60 秒重试一次" or "连接已中断，正在重连…", 2)
        end
        local serial = generation
        busy, due, stable = true, nil, 0
        local function valid()
            local active = mp.get_property("path")
            return generation == serial and (active == current or (not active and failed))
        end
        resolve_source(path, function(new_path)
            if not valid() then return end
            if not manual and (mp.get_property_native("pause") or blocked()) then
                busy = false
                return
            end
            next_path = new_path
            failed = false
            mp.commandv("loadfile", new_path, "replace")
        end, function()
            if not valid() then return end
            busy = false
            failed = failed or not manual or mp.get_property_native("eof-reached")
            if manual then mp.osd_message("刷新直播地址失败，可稍后再试", 4) end
        end)
    end

    mp.register_event("start-file", function()
        local path = mp.get_property("path")
        if next_path and path == next_path then
            origin = path
            next_path = nil
        elseif not origin and not stopped then
            origin = path
        else
            cancel()
        end
    end)

    mp.register_event("file-loaded", function()
        if not stopped then origin = origin or mp.get_property("path") end
        busy, failed = false, false
        previous_pos, stable = nil, 0
        if enabled and not stopped and origin and not owns_idle then
            -- Keep a failed reopen available for the next bounded attempt. An
            -- explicit stop restores the user's original mpv idle behaviour.
            mp.set_property("idle", "yes")
            owns_idle = true
        end
    end)

    mp.register_event("end-file", function(event)
        if event.reason == "quit" then
            cancel()
        elseif event.reason == "stop" and not next_path then
            cancel()
        elseif event.reason == "error" or event.reason == "eof" then
            failed, busy = true, false
        end
    end)
    mp.register_event("shutdown", cancel)
    mp.add_key_binding(nil, "reload", function() reload(true) end)
    if not enabled then return end

    mp.add_periodic_timer(1, function()
        if stopped or busy or not origin or mp.get_property_native("pause") then return end
        local terminal = failed or mp.get_property_native("eof-reached")
        if not terminal then
            due = nil
            local pos = mp.get_property_number("time-pos")
            if pos and previous_pos and pos > previous_pos and pos - previous_pos < 3 then
                stable = stable + (pos - previous_pos)
                if stable >= 15 then attempts = 0 end
            else
                stable = 0
            end
            previous_pos = pos
            return
        end
        previous_pos, stable = nil, 0
        if blocked() then due = nil; return end
        local now = mp.get_time()
        due = due or (now + delays[math.min(attempts + 1, #delays)])
        if now >= due then reload(false) end
    end)
end

install_recovery(function(path, load) load(path) end)

mp.add_key_binding(nil, "jump_live", function()
    local cache_end = mp.get_property_number("demuxer-cache-time")
    if mp.get_property_native("seekable") and cache_end and cache_end > 0 then
        mp.commandv("seek", cache_end, "absolute", "exact")
        mp.osd_message("已跳到直播最新位置", 2)
    else
        mp.commandv("script-binding", "live_resync/reload")
    end
end)

local auto_timer
mp.add_key_binding(nil, "auto_toggle", function()
    if auto_timer then
        auto_timer:kill()
        auto_timer = nil
        mp.osd_message("自动追直播已关闭", 2)
    else
        auto_timer = mp.add_periodic_timer(5, function()
            local pos = mp.get_property_number("time-pos")
            local edge = mp.get_property_number("demuxer-cache-time")
            if not mp.get_property_native("pause") and pos and edge and edge - pos > 6 then
                mp.commandv("seek", edge, "absolute", "exact")
            end
        end)
        mp.osd_message("自动追直播已开启", 2)
    end
end)
