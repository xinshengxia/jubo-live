-- thumbfast.lua
--
-- High-performance on-the-fly thumbnailer
--
-- Built for easy integration in third-party UIs.
-- Local network preview fixes: inherit effective request options, debounce
-- remote seeks, and remove the old overlay when the requested time changes.
-- Modified 2026-10-26; original MPL-2.0 notice is preserved below.

--[[
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. If a copy of the MPL was not distributed with this
file, You can obtain one at https://mozilla.org/MPL/2.0/.
]]

local options = {
    -- Socket path (leave empty for auto)
    socket = "",

    -- Thumbnail path (leave empty for auto)
    thumbnail = "",

    -- Maximum thumbnail generation size in pixels (scaled down to fit)
    -- Values are scaled when hidpi is enabled
    max_height = 200,
    max_width = 200,

    -- Scale factor for thumbnail display size (requires mpv 0.38+)
    -- Note that this is lower quality than increasing max_height and max_width
    scale_factor = 1,

    -- Apply tone-mapping, no to disable
    tone_mapping = "auto",

    -- Overlay id
    overlay_id = 42,

    -- Spawn thumbnailer on file load for faster initial thumbnails
    spawn_first = false,

    -- Close thumbnailer process after an inactivity period in seconds, 0 to disable
    quit_after_inactivity = 0,

    -- Enable on network playback
    network = false,

    -- Enable on audio playback
    audio = false,

    -- Enable hardware decoding
    hwdec = false,

    -- Windows only: use native Windows API to write to pipe (requires LuaJIT)
    direct_io = false,

    -- Custom path to the mpv executable
    mpv_path = "mpv",

    -- Unseekable live streams need frames from this player's own timeline.
    live_interval = 2,
    -- Retain this playback session on disk without an age or size cutoff.
    -- Positive values opt into limiting the retained index; 0 is unlimited.
    live_max_frames = 0,
    live_max_bytes = 0,
    vod_interval = 5
}

mp.utils = require "mp.utils"
mp.options = require "mp.options"
mp.options.read_options(options, "thumbfast")

local properties = {}
local pre_0_30_0 = mp.command_native_async == nil
local pre_0_33_0 = true
local support_media_control = mp.get_property_native("media-controls") ~= nil

function subprocess(args, async, callback)
    callback = callback or function() end

    if not pre_0_30_0 then
        -- FIXME: figure out which exact env var needs to be stripped. (PR: #144, Issue: #106 and #139)
        local env = os_name == "darwin" and "PATH="..os.getenv("PATH") or nil
        if async then
            return mp.command_native_async({name = "subprocess", playback_only = true, args = args, env = env}, callback)
        else
            return mp.command_native({name = "subprocess", playback_only = false, capture_stdout = true, args = args, env = env})
        end
    else
        if async then
            return mp.utils.subprocess_detached({args = args}, callback)
        else
            return mp.utils.subprocess({args = args})
        end
    end
end

local winapi = {}
if options.direct_io then
    local ffi_loaded, ffi = pcall(require, "ffi")
    if ffi_loaded then
        winapi = {
            ffi = ffi,
            C = ffi.C,
            bit = require("bit"),
            socket_wc = "",

            -- WinAPI constants
            CP_UTF8 = 65001,
            GENERIC_WRITE = 0x40000000,
            OPEN_EXISTING = 3,
            FILE_FLAG_WRITE_THROUGH = 0x80000000,
            FILE_FLAG_NO_BUFFERING = 0x20000000,
            PIPE_NOWAIT = ffi.new("unsigned long[1]", 0x00000001),

            INVALID_HANDLE_VALUE = ffi.cast("void*", -1),

            -- don't care about how many bytes WriteFile wrote, so allocate something to store the result once
            _lpNumberOfBytesWritten = ffi.new("unsigned long[1]"),
        }
        -- cache flags used in run() to avoid bor() call
        winapi._createfile_pipe_flags = winapi.bit.bor(winapi.FILE_FLAG_WRITE_THROUGH, winapi.FILE_FLAG_NO_BUFFERING)

        ffi.cdef[[
            void* __stdcall CreateFileW(const wchar_t *lpFileName, unsigned long dwDesiredAccess, unsigned long dwShareMode, void *lpSecurityAttributes, unsigned long dwCreationDisposition, unsigned long dwFlagsAndAttributes, void *hTemplateFile);
            bool __stdcall WriteFile(void *hFile, const void *lpBuffer, unsigned long nNumberOfBytesToWrite, unsigned long *lpNumberOfBytesWritten, void *lpOverlapped);
            bool __stdcall CloseHandle(void *hObject);
            bool __stdcall SetNamedPipeHandleState(void *hNamedPipe, unsigned long *lpMode, unsigned long *lpMaxCollectionCount, unsigned long *lpCollectDataTimeout);
            int __stdcall MultiByteToWideChar(unsigned int CodePage, unsigned long dwFlags, const char *lpMultiByteStr, int cbMultiByte, wchar_t *lpWideCharStr, int cchWideChar);
        ]]

        winapi.MultiByteToWideChar = function(MultiByteStr)
            if MultiByteStr then
                local utf16_len = winapi.C.MultiByteToWideChar(winapi.CP_UTF8, 0, MultiByteStr, -1, nil, 0)
                if utf16_len > 0 then
                    local utf16_str = winapi.ffi.new("wchar_t[?]", utf16_len)
                    if winapi.C.MultiByteToWideChar(winapi.CP_UTF8, 0, MultiByteStr, -1, utf16_str, utf16_len) > 0 then
                        return utf16_str
                    end
                end
            end
            return ""
        end

    else
        options.direct_io = false
    end
end

local file
local file_bytes = 0
local spawned = false
local disabled = false
local force_disabled = false
local spawn_waiting = false
local spawn_working = false
local script_written = false

local dirty = false

local x, y
local last_x, last_y

local last_seek_time

local effective_w, effective_h = options.max_width, options.max_height
local real_w, real_h
local last_real_w, last_real_h

local script_name

local show_thumbnail = false

local filters_reset = {["lavfi-crop"]=true, ["crop"]=true}
local filters_runtime = {["hflip"]=true, ["vflip"]=true}
local filters_all = {["hflip"]=true, ["vflip"]=true, ["lavfi-crop"]=true, ["crop"]=true}

local tone_mappings = {["none"]=true, ["clip"]=true, ["linear"]=true, ["gamma"]=true, ["reinhard"]=true, ["hable"]=true, ["mobius"]=true}
local last_tone_mapping

local last_vf_reset = ""
local last_vf_runtime = ""

local last_rotate = 0

local par = ""
local last_par = ""

local last_crop = nil

local last_has_vid = 0
local has_vid = 0

local file_timer
local file_check_period = 1/60

local allow_fast_seek = true

local client_script = [=[
#!/usr/bin/env bash
MPV_IPC_FD=0; MPV_IPC_PATH="%s"
trap "kill 0" EXIT
while [[ $# -ne 0 ]]; do case $1 in --mpv-ipc-fd=*) MPV_IPC_FD=${1/--mpv-ipc-fd=/} ;; esac; shift; done
if echo "print-text thumbfast" >&"$MPV_IPC_FD"; then echo -n > "$MPV_IPC_PATH"; tail -f "$MPV_IPC_PATH" >&"$MPV_IPC_FD" & while read -r -u "$MPV_IPC_FD" 2>/dev/null; do :; done; fi
]=]

local function get_os()
    local raw_os_name = ""

    if jit and jit.os and jit.arch then
        raw_os_name = jit.os
    else
        if package.config:sub(1,1) == "\\" then
            -- Windows
            local env_OS = os.getenv("OS")
            if env_OS then
                raw_os_name = env_OS
            end
        else
            raw_os_name = subprocess({"uname", "-s"}).stdout
        end
    end

    raw_os_name = (raw_os_name):lower()

    local os_patterns = {
        ["windows"] = "windows",
        ["linux"]   = "linux",

        ["osx"]     = "darwin",
        ["mac"]     = "darwin",
        ["darwin"]  = "darwin",

        ["^mingw"]  = "windows",
        ["^cygwin"] = "windows",

        ["bsd$"]    = "darwin",
        ["sunos"]   = "darwin"
    }

    -- Default to linux
    local str_os_name = "linux"

    for pattern, name in pairs(os_patterns) do
        if raw_os_name:match(pattern) then
            str_os_name = name
            break
        end
    end

    return str_os_name
end

local os_name = mp.get_property("platform") or get_os()

local path_separator = os_name == "windows" and "\\" or "/"

if options.socket == "" then
    if os_name == "windows" then
        options.socket = "thumbfast"
    else
        options.socket = "/tmp/thumbfast"
    end
end

if options.thumbnail == "" then
    if os_name == "windows" then
        options.thumbnail = os.getenv("TEMP").."\\thumbfast.out"
    else
        options.thumbnail = "/tmp/thumbfast.out"
    end
end

local unique = mp.utils.getpid()

options.socket = options.socket .. unique
options.thumbnail = options.thumbnail .. unique

if options.direct_io then
    if os_name == "windows" then
        winapi.socket_wc = winapi.MultiByteToWideChar("\\\\.\\pipe\\" .. options.socket)
    end

    if winapi.socket_wc == "" then
        options.direct_io = false
    end
end

options.scale_factor = math.floor(options.scale_factor)

local mpv_path = options.mpv_path
local frontend_path

if mpv_path == "mpv" and os_name == "windows" then
    frontend_path = mp.get_property_native("user-data/frontend/process-path")
    mpv_path = os.getenv("JUBO_THUMBFAST_MPV") or frontend_path or mpv_path
end

if mpv_path == "mpv" and os_name == "darwin" and unique then
    -- TODO: look into ~~osxbundle/
    mpv_path = string.gsub(subprocess({"ps", "-o", "comm=", "-p", tostring(unique)}).stdout, "[\n\r]", "")
    if mpv_path ~= "mpv" then
        mpv_path = string.gsub(mpv_path, "/mpv%-bundle$", "/mpv")
        local mpv_bin = mp.utils.file_info("/usr/local/mpv")
        if mpv_bin and mpv_bin.is_file then
            mpv_path = "/usr/local/mpv"
        else
            local mpv_app = mp.utils.file_info("/Applications/mpv.app/Contents/MacOS/mpv")
            if mpv_app and mpv_app.is_file then
                mp.msg.warn("symlink mpv to fix Dock icons: `sudo ln -s /Applications/mpv.app/Contents/MacOS/mpv /usr/local/mpv`")
            else
                mp.msg.warn("drag to your Applications folder and symlink mpv to fix Dock icons: `sudo ln -s /Applications/mpv.app/Contents/MacOS/mpv /usr/local/mpv`")
            end
        end
    end
end

local function vo_tone_mapping()
    local passes = mp.get_property_native("vo-passes")
    if passes and passes["fresh"] then
        for k, v in pairs(passes["fresh"]) do
            for k2, v2 in pairs(v) do
                if k2 == "desc" and v2 then
                    local tone_mapping = string.match(v2, "([0-9a-z.-]+) tone map")
                    if tone_mapping then
                        return tone_mapping
                    end
                end
            end
        end
    end
end

local function vf_string(filters, full)
    local vf = ""
    local vf_table = properties["vf"]

    if (properties["video-crop"] or "") ~= "" then
        vf = "lavfi-crop="..string.gsub(properties["video-crop"], "(%d*)x?(%d*)%+(%d+)%+(%d+)", "w=%1:h=%2:x=%3:y=%4")..","
        local width = properties["video-out-params"] and properties["video-out-params"]["dw"]
        local height = properties["video-out-params"] and properties["video-out-params"]["dh"]
        if width and height then
            vf = string.gsub(vf, "w=:h=:", "w="..width..":h="..height..":")
        end
    end

    if vf_table and #vf_table > 0 then
        for i = #vf_table, 1, -1 do
            if filters[vf_table[i].name] then
                local args = ""
                for key, value in pairs(vf_table[i].params) do
                    if args ~= "" then
                        args = args .. ":"
                    end
                    args = args .. key .. "=" .. value
                end
                vf = vf .. vf_table[i].name .. "=" .. args .. ","
            end
        end
    end

    if (full and options.tone_mapping ~= "no") or options.tone_mapping == "auto" then
        if properties["video-params"] and properties["video-params"]["primaries"] == "bt.2020" then
            local tone_mapping = options.tone_mapping
            if tone_mapping == "auto" then
                tone_mapping = last_tone_mapping or properties["tone-mapping"]
                if tone_mapping == "auto" and properties["current-vo"] == "gpu-next" then
                    tone_mapping = vo_tone_mapping()
                end
            end
            if not tone_mappings[tone_mapping] then
                tone_mapping = "hable"
            end
            last_tone_mapping = tone_mapping
            vf = vf .. "zscale=transfer=linear,format=gbrpf32le,tonemap="..tone_mapping..",zscale=transfer=bt709,"
        end
    end

    if full then
        vf = vf.."scale=w="..effective_w..":h="..effective_h..par..",pad=w="..effective_w..":h="..effective_h..":x=-1:y=-1,format=bgra"
    end

    return vf
end

local function calc_dimensions()
    local width = properties["video-out-params"] and properties["video-out-params"]["dw"]
    local height = properties["video-out-params"] and properties["video-out-params"]["dh"]
    if not width or not height then return end

    local scale = properties["display-hidpi-scale"] or 1

    if width / height > options.max_width / options.max_height then
        effective_w = math.floor(options.max_width * scale + 0.5)
        effective_h = math.floor(height / width * effective_w + 0.5)
    else
        effective_h = math.floor(options.max_height * scale + 0.5)
        effective_w = math.floor(width / height * effective_h + 0.5)
    end
    -- HiDPI thumbnails must still fit a small player window above its seekbar.
    local osd = properties["osd-dimensions"]
    if osd and (osd.w or 0) > 0 and (osd.h or 0) > 0 then
        local fit = math.min(1, osd.w * 0.8 / (effective_w * options.scale_factor), osd.h * 0.5 / (effective_h * options.scale_factor))
        effective_w = math.max(2, math.floor(effective_w * fit))
        effective_h = math.max(2, math.floor(effective_h * fit))
    end

    local v_par = properties["video-out-params"] and properties["video-out-params"]["par"] or 1
    if v_par == 1 then
        par = ":force_original_aspect_ratio=decrease"
    else
        par = ""
    end
end

local info_timer = nil

local function info(w, h)
    local rotate = properties["video-params"] and properties["video-params"]["rotate"]
    local image = properties["current-tracks/video"] and properties["current-tracks/video"]["image"]
    local albumart = image and properties["current-tracks/video"]["albumart"]

    disabled = (w or 0) == 0 or (h or 0) == 0 or
        has_vid == 0 or
        (properties["demuxer-via-network"] and not options.network) or
        (albumart and not options.audio) or
        (image and not albumart) or
        force_disabled

    if info_timer then
        info_timer:kill()
        info_timer = nil
    elseif has_vid == 0 or (rotate == nil and not disabled) then
        info_timer = mp.add_timeout(0.05, function() info(w, h) end)
    end

    local json, err = mp.utils.format_json({width=w * options.scale_factor, height=h * options.scale_factor, scale_factor=options.scale_factor, disabled=disabled, available=true, socket=options.socket, thumbnail=options.thumbnail, overlay_id=options.overlay_id})
    if pre_0_30_0 then
        mp.command_native({"script-message", "thumbfast-info", json})
    else
        mp.command_native_async({"script-message", "thumbfast-info", json}, function() end)
    end
end

local function remove_thumbnail_files()
    if file then
        file:close()
        file = nil
        file_bytes = 0
    end
    os.remove(options.thumbnail)
    os.remove(options.thumbnail..".bgra")
    os.remove(options.thumbnail..".tmp")
end

local activity_timer

-- LOCAL: preview worker health probe and bounded retry.
-- A network preview worker can be refused on its first open (for example a
-- tiny "Access Denied" body instead of the video). mpv keeps such a worker
-- alive but with no media loaded, so every later seek is a no-op and the
-- preview stays black forever. A tiny generated script inside the worker
-- reports its load state through a marker file; a failed worker is killed and
-- rebuilt with backoff, a bounded number of times per file load.
local worker_loaded = false
local worker_failures = 0
local worker_restart_timer
local worker_stopping = false
local worker_generation = 0
local worker_command
local worker_spawn_time = 0
local schedule_worker_restart
local WORKER_MAX_RETRIES = 3
local WORKER_OPEN_TIMEOUT = 35

local function worker_probe_paths()
    local probe = options.thumbnail .. ".probe.lua"
    return probe, probe .. ".marker"
end

local function write_worker_probe()
    local probe, marker = worker_probe_paths()
    local file = io.open(probe, "w")
    if file == nil then return nil end
    -- The marker path is derived from the probe's own filename, so no path
    -- escaping is needed. Each report overwrites the previous one.
    file:write([=[
local source = (debug.getinfo(1, "S").source or ""):gsub("^@", "")
local marker = source .. ".marker"
local function report(value)
    local handle = io.open(marker, "w")
    if handle then handle:write(value) handle:close() end
end
report("start")
mp.register_event("file-loaded", function() report("loaded") end)
mp.register_event("end-file", function(event) report("error:" .. tostring(event.reason)) end)
]=])
    file:close()
    return probe, marker
end

-- LOCAL: a web media adapter can expose the media it actually opened. mpv's "path"
-- property stays the web page URL, and a locally cached HLS manifest reports
-- demuxer-via-network=false, so the upstream path/ytdl logic cannot find the
-- real stream for the preview worker.
local function webvideo_media()
    local data = mp.get_property_native("user-data/webvideo-mpv")
    if type(data) ~= "table" or data.status ~= "ready" then return nil end
    local media = data.media
    if type(media) ~= "string" or media == "" then return nil end
    return data
end

local function file_option(name)
    local value = mp.get_property_native("file-local-options/" .. name)
    if value == nil then value = mp.get_property_native("options/" .. name) end
    return value
end

local function live_preview_enabled()
    return options.network and not webvideo_media() and
        mp.get_property_native("demuxer-via-network") == true and
        mp.get_property_native("seekable") == false
end

local function frame_capture_enabled()
    local webvideo = mp.get_property_native("user-data/webvideo-mpv")
    return options.network and
        not (type(webvideo) == "table" and webvideo.status == "ready") and
        mp.get_property_native("demuxer-via-network") == true
end

local function shared_cached_media()
    local data = webvideo_media()
    if type(data) == "table" and data.status == "ready" and data.media_cache and
        type(data.media) == "string" and
        data.media:match("^http://127%.0%.0%.1[:/]") then return data end
end

local function spawn(time)
    if disabled then return end
    -- A second live connection has a different buffer and timeline. Seeking
    -- it to timestamps from the main player cannot preview cached history.
    if live_preview_enabled() then return end
    if shared_cached_media() then return end

    local path = properties["path"]
    if path == nil then return end

    if options.quit_after_inactivity > 0 then
        if show_thumbnail or activity_timer:is_enabled() then
            activity_timer:kill()
        end
        activity_timer:resume()
    end

    local open_filename = properties["stream-open-filename"]
    local ytdl = open_filename and properties["demuxer-via-network"] and path ~= open_filename
    if ytdl then
        path = open_filename
    end

    local webvideo = webvideo_media()
    if webvideo then
        -- A cache-enabled loopback proxy shares the main player's downloaded
        -- bytes and must be tried first. Older adapters without media_cache
        -- retain their direct-first order; rebuilds rotate through fallbacks.
        local local_proxy = type(webvideo.media) == "string" and webvideo.media:match("^http://127%.0%.0%.1[:/]") ~= nil
        local alternate = webvideo.media_alt
        if type(alternate) ~= "string" or alternate == "" or alternate == webvideo.media then
            alternate = nil
        end
        local primary = (local_proxy and alternate and not webvideo.media_cache) and alternate or webvideo.media
        local candidates = { primary }
        if alternate and alternate ~= primary then candidates[#candidates+1] = alternate end
        if webvideo.media ~= primary then candidates[#candidates+1] = webvideo.media end
        local open_media = properties["stream-open-filename"]
        if open_media and open_media ~= webvideo.media and open_media:match("^%a+://") then
            candidates[#candidates+1] = open_media
        end
        path = candidates[(worker_failures % #candidates) + 1]
    end

    local probe, marker = write_worker_probe()
    if marker then os.remove(marker) end
    remove_thumbnail_files()

    local vid = properties["vid"]
    has_vid = vid or 0

    local args = {
        mpv_path, "--no-config", "--msg-level=all=no", "--idle", "--pause", "--keep-open=always", "--really-quiet", "--no-terminal",
        "--load-scripts=no", "--osc=no", "--ytdl=no", "--load-stats-overlay=no", "--load-osd-console=no", "--load-auto-profiles=no",
        "--edition="..(properties["edition"] or "auto"), "--vid="..(vid or "auto"), "--no-sub", "--no-audio",
        "--start="..time, allow_fast_seek and "--hr-seek=no" or "--hr-seek=yes",
        "--ytdl-format=worst", "--demuxer-readahead-secs=0", "--demuxer-max-bytes=128KiB",
        "--vd-lavc-skiploopfilter=all", "--vd-lavc-software-fallback=1", "--vd-lavc-fast", "--vd-lavc-threads=2", "--hwdec="..(options.hwdec and "auto" or "no"),
        "--vf="..vf_string(filters_all, true),
        "--sws-scaler=fast-bilinear",
        "--video-rotate="..last_rotate,
        "--ovc=rawvideo", "--of=image2", "--ofopts=update=1", "--o="..options.thumbnail
    }

    if probe then
        -- --load-scripts=no still accepts an explicit --script, so the worker
        -- can report its own open state through the marker file.
        table.insert(args, "--script=" .. probe)
    end

    if not pre_0_30_0 then
        table.insert(args, "--sws-allow-zimg=no")
    end

    if properties["demuxer-via-network"] or webvideo then
        local bypass_cache = webvideo and webvideo.media_cache and path ~= webvideo.media
        -- The worker uses --no-config; carry over the active file's request
        -- context instead of making unauthenticated/default-agent requests.
        for _, name in ipairs({"referrer", "user-agent", "http-proxy", "cookies",
                "cookies-file", "tls-verify", "force-seekable", "network-timeout"}) do
            local value = file_option(name)
            if name == "http-proxy" and webvideo and webvideo.media_cache then
                value = bypass_cache and webvideo.media_proxy or ''
                if bypass_cache and not webvideo.media_proxy then value = nil end
            end
            if type(value) == "boolean" then value = value and "yes" or "no" end
            if value ~= nil and (value ~= "" or (name == "http-proxy" and webvideo and webvideo.media_cache and not bypass_cache)) then
                table.insert(args, "--" .. name .. "=" .. tostring(value))
            end
        end
        local headers = file_option("http-header-fields")
        if type(headers) == "table" then
            for _, header in ipairs(headers) do
                -- -append accepts one field and preserves commas in its value.
                table.insert(args, "--http-header-fields-append=" .. header)
            end
        end
        -- Some validated HLS providers use non-media segment suffixes. Carry
        -- the parent file's scoped format settings into the preview worker.
        local lavf = file_option("demuxer-lavf-o")
        if type(lavf) == "table" then
            for _, name in ipairs({"allowed_extensions", "allowed_segment_extensions",
                    "extension_picky", "protocol_whitelist", "http_seekable", "seg_max_retry"}) do
                local value = lavf[name]
                if name == "http_seekable" and bypass_cache and webvideo.hls then value = '0' end
                if value ~= nil then table.insert(args, "--demuxer-lavf-o-append=" .. name .. "=" .. tostring(value)) end
            end
        end
        -- 128 KiB can be smaller than one compressed video packet/GOP.
        local stream = file_option("stream-lavf-o")
        if type(stream) == "table" and stream.seekable ~= nil then
            table.insert(args, "--stream-lavf-o-append=seekable=" .. tostring(stream.seekable))
        end
        if webvideo and webvideo.hls then
            -- A locally cached HLS manifest needs the explicit demuxer the main
            -- file uses; mpv cannot rely on network detection for a local path.
            table.insert(args, "--demuxer=lavf")
            table.insert(args, "--demuxer-lavf-format=hls")
        end
        table.insert(args, "--demuxer-max-bytes=32MiB")
        table.insert(args, "--demuxer-readahead-secs=2")
        table.insert(args, "--cache-secs=2")
        table.insert(args, "--cache-on-disk=no")
        -- Preview only needs a small frame: use the lowest HLS variant when the
        -- source is an adaptive playlist, otherwise remote seeks are much slower.
        -- Shared HLS cache hits require the same rendition as the main player.
        -- Selecting min here would request a different set of uncached segments.
        table.insert(args, "--hls-bitrate=" .. tostring((webvideo and webvideo.media_cache and (file_option("hls-bitrate") or "max")) or "min"))
        table.insert(args, "--hr-seek=yes")
    end

    if support_media_control then
        table.insert(args, "--media-controls=no")
    end

    if os_name == "darwin" and properties["macos-app-activation-policy"] then
        table.insert(args, "--macos-app-activation-policy=accessory")
    end

    if os_name == "windows" or pre_0_33_0 then
        table.insert(args, "--input-ipc-server="..options.socket)
    elseif not script_written then
        local client_script_path = options.socket..".run"
        local script = io.open(client_script_path, "w+")
        if script == nil then
            mp.msg.error("client script write failed")
            return
        else
            script_written = true
            script:write(string.format(client_script, options.socket))
            script:close()
            subprocess({"chmod", "+x", client_script_path}, true)
            table.insert(args, "--scripts="..client_script_path)
        end
    else
        local client_script_path = options.socket..".run"
        table.insert(args, "--scripts="..client_script_path)
    end

    table.insert(args, "--")
    table.insert(args, path)

    spawned = true
    spawn_waiting = true
    worker_loaded = false
    worker_spawn_time = mp.get_time()
    worker_generation = worker_generation + 1
    worker_stopping = false
    local generation = worker_generation

    worker_command = subprocess(args, true,
        function(success, result)
            if worker_generation ~= generation then return end
            worker_command = nil
            -- LOCAL: an intentional stop is not a failure.
            if worker_stopping then
                worker_stopping = false
                return
            end
            if spawned and not spawn_waiting then
                -- Worker died after a successful start; rebuild on the next
                -- hover instead of keeping a dead pipe forever.
                spawned = false
                if show_thumbnail and last_seek_time and not disabled then
                    schedule_worker_restart(0.5)
                end
                return
            end
            if spawn_waiting and (success == false or (result.status ~= 0 and result.status ~= -2)) then
                spawned = false
                spawn_waiting = false
                options.tone_mapping = "no"
                mp.msg.error("mpv subprocess create failed")
                if not spawn_working then -- notify users of required configuration
                    if options.mpv_path == "mpv" then
                        if properties["current-vo"] == "libmpv" then
                            if options.mpv_path == mpv_path then -- attempt to locate ImPlay
                                mpv_path = "ImPlay"
                                spawn(time)
                            else -- ImPlay not in path
                                if os_name ~= "darwin" then
                                    force_disabled = true
                                    info(real_w or effective_w, real_h or effective_h)
                                end
                                mp.commandv("show-text", "thumbfast: ERROR! cannot create mpv subprocess", 5000)
                                mp.commandv("script-message-to", "implay", "show-message", "thumbfast initial setup", "Set mpv_path=PATH_TO_ImPlay in thumbfast config:\n" .. string.gsub(mp.command_native({"expand-path", "~~/script-opts/thumbfast.conf"}), "[/\\]", path_separator).."\nand restart ImPlay")
                            end
                        else
                            mp.commandv("show-text", "thumbfast: ERROR! cannot create mpv subprocess", 5000)
                            if os_name == "windows" and frontend_path == nil then
                                mp.commandv("script-message-to", "mpvnet", "show-text", "thumbfast: ERROR! install standalone mpv, see README", 5000, 20)
                                mp.commandv("script-message", "mpv.net", "show-text", "thumbfast: ERROR! install standalone mpv, see README", 5000, 20)
                            end
                        end
                    else
                        mp.commandv("show-text", "thumbfast: ERROR! cannot create mpv subprocess", 5000)
                        -- found ImPlay but not defined in config
                        mp.commandv("script-message-to", "implay", "show-message", "thumbfast", "Set mpv_path=PATH_TO_ImPlay in thumbfast config:\n" .. string.gsub(mp.command_native({"expand-path", "~~/script-opts/thumbfast.conf"}), "[/\\]", path_separator).."\nand restart ImPlay")
                    end
                end
            elseif success == true and (result.status == 0 or result.status == -2) then
                if not spawn_working and properties["current-vo"] == "libmpv" and options.mpv_path ~= mpv_path then
                    mp.commandv("script-message-to", "implay", "show-message", "thumbfast initial setup", "Set mpv_path=ImPlay in thumbfast config:\n" .. string.gsub(mp.command_native({"expand-path", "~~/script-opts/thumbfast.conf"}), "[/\\]", path_separator).."\nand restart ImPlay")
                end
                spawn_working = true
                spawn_waiting = false
            end
        end
    )
end

local function run(command)
    if not spawned then return end

    if options.direct_io then
        local hPipe = winapi.C.CreateFileW(winapi.socket_wc, winapi.GENERIC_WRITE, 0, nil, winapi.OPEN_EXISTING, winapi._createfile_pipe_flags, nil)
        if hPipe ~= winapi.INVALID_HANDLE_VALUE then
            local buf = command .. "\n"
            winapi.C.SetNamedPipeHandleState(hPipe, winapi.PIPE_NOWAIT, nil, nil)
            winapi.C.WriteFile(hPipe, buf, #buf + 1, winapi._lpNumberOfBytesWritten, nil)
            winapi.C.CloseHandle(hPipe)
        end

        return
    end

    local command_n = command.."\n"

    if os_name == "windows" then
        if file and file_bytes + #command_n >= 4096 then
            file:close()
            file = nil
            file_bytes = 0
        end
        if not file then
            file = io.open("\\\\.\\pipe\\"..options.socket, "r+b")
        end
    elseif pre_0_33_0 then
        subprocess({"/usr/bin/env", "sh", "-c", "echo '" .. command .. "' | socat - " .. options.socket})
        return
    elseif not file then
        file = io.open(options.socket, "r+")
    end
    if file then
        file_bytes = file:seek("end")
        file:write(command_n)
        file:flush()
    end
end

-- LOCAL: rebuild a preview worker that never managed to open its media.
local function stop_worker()
    if not spawned and not worker_command then return end
    worker_stopping = true
    -- An open blocked inside the demuxer may never process an IPC quit.
    if worker_command and mp.abort_async_command then
        pcall(mp.abort_async_command, worker_command)
        worker_command = nil
    else
        pcall(run, "quit")
    end
    spawned = false
    spawn_waiting = false
    remove_thumbnail_files()
end

schedule_worker_restart = function(delay)
    if worker_restart_timer then worker_restart_timer:kill() end
    worker_restart_timer = mp.add_timeout(math.max(delay or 0.5, 0.05), function()
        worker_restart_timer = nil
        if disabled or not show_thumbnail or last_seek_time == nil then return end
        if spawned then return end
        if worker_failures >= WORKER_MAX_RETRIES then
            mp.msg.warn("thumbfast: preview worker failed " .. tostring(worker_failures) ..
                " times; giving up until the next file load")
            return
        end
        worker_failures = worker_failures + 1
        mp.msg.warn("thumbfast: rebuilding preview worker (attempt " .. tostring(worker_failures) ..
            "/" .. tostring(WORKER_MAX_RETRIES) .. ")")
        spawn(last_seek_time)
        file_timer:resume()
    end)
end

local function worker_health_check()
    if not spawned then return end
    local _, marker = worker_probe_paths()
    local handle = io.open(marker, "r")
    if handle then
        local status = handle:read("*a") or ""
        handle:close()
        if status == "loaded" then
            worker_loaded = true
        elseif status:sub(1, 6) == "error:" then
            os.remove(marker)
            mp.msg.warn("thumbfast: preview worker reported " .. status)
            stop_worker()
            if show_thumbnail and last_seek_time and not disabled then
                schedule_worker_restart(1.0)
            end
            return
        end
    end
    -- Fallback for a worker that never reports anything (hung open).
    if not worker_loaded and worker_spawn_time > 0 and mp.get_time() - worker_spawn_time > WORKER_OPEN_TIMEOUT then
        if marker then os.remove(marker) end
        mp.msg.warn("thumbfast: preview worker open timed out")
        stop_worker()
        if show_thumbnail and last_seek_time and not disabled then
            schedule_worker_restart(1.0)
        end
    end
end

local function draw(w, h, script, thumbnail_override)
    if not w or not show_thumbnail then return end
    local thumbnail = thumbnail_override or options.thumbnail
    if x ~= nil then
        local scale_w, scale_h = options.scale_factor ~= 1 and (w * options.scale_factor) or nil, options.scale_factor ~= 1 and (h * options.scale_factor) or nil
        if pre_0_30_0 then
            mp.command_native({"overlay-add", options.overlay_id, x, y, thumbnail..".bgra", 0, "bgra", w, h, (4*w), scale_w, scale_h})
        else
            mp.command_native_async({"overlay-add", options.overlay_id, x, y, thumbnail..".bgra", 0, "bgra", w, h, (4*w), scale_w, scale_h}, function() end)
        end
    elseif script then
        local json, err = mp.utils.format_json({width=w, height=h, scale_factor=options.scale_factor, x=x, y=y, socket=options.socket, thumbnail=thumbnail, overlay_id=options.overlay_id})
        mp.commandv("script-message-to", script, "thumbfast-render", json)
    end
end

-- LOCAL: capture the main player's already decoded live frames. This adds no
-- second stream connection and never seeks or pauses the main player.
local live_frames, live_buckets = {}, {}
local live_bytes, live_loaded, live_shown = 0, false, nil
local live_interval = math.max(1, tonumber(options.live_interval) or 2)
local vod_interval = math.max(1, tonumber(options.vod_interval) or 5)
local live_max_frames = math.max(0, math.floor(tonumber(options.live_max_frames) or 0))
local live_max_bytes = math.max(0, tonumber(options.live_max_bytes) or 0)
local live_store, live_store_failed
local live_store_path = options.thumbnail .. ".history.bgra"

local function live_store_error()
    if not live_store_failed then
        mp.msg.warn("thumbfast: session thumbnail disk cache unavailable")
    end
    live_store_failed = true
end
local frame_cache_active = false
local shot_command, shot_timer, shot_deadline, shot_target, shot_failure_target
local shot_serial, shot_failures = 0, 0
local shot_files = {}

local function cancel_shot()
    shot_serial = shot_serial + 1
    if shot_timer then shot_timer:kill(); shot_timer = nil end
    if shot_deadline then shot_deadline:kill(); shot_deadline = nil end
    if shot_command then pcall(mp.abort_async_command, shot_command); shot_command = nil end
    shot_target = nil
    for path in pairs(shot_files) do os.remove(path) end
    shot_files = {}
end

local function frame_interval()
    return live_preview_enabled() and live_interval or vod_interval
end

local function live_cache_reset()
    cancel_shot()
    shot_failures = 0
    shot_failure_target = nil
    if live_store then live_store:close(); live_store = nil end
    os.remove(live_store_path)
    live_store_failed = false
    live_frames, live_buckets = {}, {}
    live_bytes, live_shown, frame_cache_active = 0, nil, false
    os.remove(options.thumbnail .. ".live-tmp")
    os.remove(options.thumbnail .. ".cache.bgra")
    mp.set_property_native("user-data/thumbfast-live-cache", nil)
end

local function cache_frame(data, w, h, time, bucket, kind)
    if live_buckets[bucket] or live_store_failed then return end
    if not live_store then live_store = io.open(live_store_path, "w+b") end
    if not live_store then live_store_error(); return end
    local offset = live_store:seek("end")
    if not offset or not live_store:write(data) or not live_store:flush() then
        live_store_error(); return
    end
    -- Only the index stays in memory; BGRA payloads live in this session file.
    local frame = {t=time, bucket=bucket, w=w, h=h, offset=offset,
        bytes=#data, kind=kind}
    live_frames[#live_frames + 1] = frame
    live_buckets[bucket] = frame
    live_bytes = live_bytes + #data
end

local function live_drop(index)
    local frame = table.remove(live_frames, index)
    if frame then
        live_bytes = live_bytes - frame.bytes
        if live_buckets[frame.bucket] == frame then live_buckets[frame.bucket] = nil end
    end
end

local function live_prune()
    -- Decoded thumbnails remain useful after the demuxer's rolling media
    -- buffer has moved on. Never expire them along with seekable ranges.
    while (live_max_frames > 0 and #live_frames > live_max_frames) or
        (live_max_bytes > 0 and live_bytes > live_max_bytes) do live_drop(1) end
end

local function live_capture()
    if disabled or not live_loaded or not frame_capture_enabled() then return end
    if mp.get_property_native("seeking") or mp.get_property_native("paused-for-cache") then return end
    local pos = mp.get_property_number("time-pos")
    if not pos then return end
    local interval = frame_interval()
    local bucket = math.floor(pos / interval)
    live_prune()
    if live_buckets[bucket] then return end
    -- The video-only screenshot excludes subtitles and UI. Force BGRA rather
    -- than interpreting the undefined padding byte in screenshot-raw's bgr0.
    local ok, image = pcall(mp.command_native, {"screenshot-raw", "video", "bgra"})
    if not ok or type(image) ~= "table" or type(image.data) ~= "string" then return end
    local width, height, stride = image.w, image.h, image.stride
    if not width or not height or not stride or width < 1 or height < 1 or
        stride < width * 4 or #image.data < stride * height then return end
    local tw, th = effective_w, effective_h
    if not tw or not th or tw < 1 or th < 1 then return end
    local rows = {}
    for y = 0, th - 1 do
        local base = math.floor(y * height / th) * stride
        local row = {}
        for x = 0, tw - 1 do
            local offset = base + math.floor(x * width / tw) * 4 + 1
            local b, g, r = image.data:byte(offset, offset + 2)
            row[#row + 1] = string.char(b, g, r, 255)
        end
        rows[#rows + 1] = table.concat(row)
    end
    local data = table.concat(rows)
    cache_frame(data, tw, th, pos, bucket, "played")
    live_prune()
    mp.set_property_native("user-data/thumbfast-live-cache", {
        count=#live_frames, bytes=live_bytes, interval=interval,
        latest=pos, max_frames=live_max_frames, max_bytes=live_max_bytes,
        storage="disk", unlimited=live_max_frames==0 and live_max_bytes==0,
        disk_error=live_store_failed==true
    })
end

local function live_preview_show(time, script)
    local best, distance
    for _, frame in ipairs(live_frames) do
        local delta = math.abs(frame.t - time)
        local tolerance = frame.kind == "preview" and 0.25 or math.max(frame_interval() * 0.5, 1)
        if delta <= tolerance and (distance == nil or delta < distance) then best, distance = frame, delta end
    end
    -- Never pass off an unrelated frame as the requested timestamp.
    if not best then
        real_w, real_h, live_shown = nil, nil, nil
        mp.command_native_async({"overlay-remove", options.overlay_id}, function() end)
        return false
    end
    if live_shown ~= best then
        if not live_store or not live_store:seek("set", best.offset) then return false end
        local data = live_store:read(best.bytes)
        if not data or #data ~= best.bytes then live_store_error(); return false end
        local temp = options.thumbnail .. ".live-tmp"
        local handle = io.open(temp, "wb")
        if not handle then return false end
        local wrote = handle:write(data)
        local closed = handle:close()
        if not wrote or not closed then os.remove(temp); return false end
        local destination = options.thumbnail .. ".cache.bgra"
        os.remove(destination)
        if not os.rename(temp, destination) then return false end
        live_shown = best
    end
    real_w, real_h = best.w, best.h
    draw(real_w, real_h, script, options.thumbnail .. ".cache")
    return true
end

-- The adapters' shared proxy can decode an uncovered timestamp locally. A short
-- one-frame process has an immutable start target, so an in-flight frame from
-- the previous hover can never be mislabeled as the new target.
local cached_preview_request
local function cached_preview_failed(time)
    shot_failures = shot_failures + 1
    mp.msg.warn("thumbfast: cached preview decode failed (attempt " .. shot_failures .. "/3)")
    if shot_failures < 3 and show_thumbnail and last_seek_time and
        math.abs(last_seek_time-time) <= 0.25 then
        shot_timer = mp.add_timeout(0.5, function()
            shot_timer = nil
            cached_preview_request(time)
        end)
    end
end

cached_preview_request = function(time)
    if not shot_failure_target or math.abs(shot_failure_target-time) > 0.25 then
        shot_failure_target, shot_failures = time, 0
    end
    if shot_failures >= 3 then return end
    if shot_target and math.abs(shot_target-time) < 0.05 and (shot_timer or shot_command) then return end
    cancel_shot()
    shot_target = time
    local serial = shot_serial
    shot_timer = mp.add_timeout(0.12, function()
        shot_timer = nil
        local media = shared_cached_media()
        if not media or serial ~= shot_serial then return end
        local output = options.thumbnail .. ".shot-" .. serial
        shot_files[output] = true
        local w, h = effective_w, effective_h
        local path = media.media
        local alternate = media.media_alt
        local bypass_cache = shot_failures >= 2 and type(alternate) == "string" and
            alternate ~= "" and alternate ~= path
        if bypass_cache then path = alternate end
        local args = {mpv_path, "--no-config", "--load-scripts=no", "--no-audio", "--no-sub",
            "--ytdl=no", "--media-controls=no", "--really-quiet", "--no-terminal", "--hwdec=no",
            "--start="..time, "--frames=1", "--hr-seek=yes", "--cache=no", "--http-proxy=",
            "--network-timeout=8", "--demuxer-readahead-secs=0", "--demuxer-max-bytes=32MiB",
            "--vf="..vf_string(filters_all, true), "--video-rotate="..last_rotate,
            "--sws-scaler=fast-bilinear", "--sws-allow-zimg=no", "--ovc=rawvideo",
            "--of=image2", "--ofopts=update=1", "--o="..output}
        if bypass_cache then
            -- Retain the adapter's direct fallback and its scoped request
            -- context if the shared proxy fails twice for this target.
            for _, name in ipairs({"referrer", "user-agent", "http-proxy", "cookies",
                    "cookies-file", "tls-verify", "force-seekable", "network-timeout"}) do
                local value = file_option(name)
                if name == "http-proxy" then value = media.media_proxy end
                if type(value) == "boolean" then value = value and "yes" or "no" end
                if value ~= nil and value ~= "" then
                    args[#args+1] = "--" .. name .. "=" .. tostring(value)
                end
            end
            for _, header in ipairs(file_option("http-header-fields") or {}) do
                args[#args+1] = "--http-header-fields-append=" .. header
            end
            local lavf = file_option("demuxer-lavf-o") or {}
            for _, name in ipairs({"allowed_extensions", "allowed_segment_extensions",
                    "extension_picky", "protocol_whitelist", "http_seekable", "seg_max_retry"}) do
                local value = lavf[name]
                if name == "http_seekable" and media.hls then value = '0' end
                if value ~= nil then args[#args+1] = "--demuxer-lavf-o-append=" .. name .. "=" .. tostring(value) end
            end
            local stream = file_option("stream-lavf-o") or {}
            if stream.seekable ~= nil then args[#args+1] = "--stream-lavf-o-append=seekable=" .. tostring(stream.seekable) end
        end
        if media.hls then
            args[#args+1] = "--demuxer=lavf"
            args[#args+1] = "--demuxer-lavf-format=hls"
            args[#args+1] = "--hls-bitrate="..tostring(file_option("hls-bitrate") or "max")
        end
        args[#args+1] = "--"
        args[#args+1] = path
        -- network-timeout bounds individual reads, not the entire decode.
        -- A loaded persistent HLS worker can otherwise remain stuck seeking
        -- forever. Each immutable request has its own total deadline.
        shot_deadline = mp.add_timeout(20, function()
            if serial ~= shot_serial then return end
            shot_deadline = nil
            cancel_shot()
            cached_preview_failed(time)
        end)
        shot_command = subprocess(args, true, function(success, result)
            if serial ~= shot_serial then
                os.remove(output);shot_files[output]=nil;return
            end
            shot_command = nil
            if shot_deadline then shot_deadline:kill(); shot_deadline = nil end
            local handle = io.open(output, "rb")
            local data = handle and handle:read("*a") or nil
            if handle then handle:close() end
            os.remove(output);shot_files[output]=nil
            if not success or not result or result.status ~= 0 or not data or #data ~= w*h*4 then
                cached_preview_failed(time)
                return
            end
            shot_failures = 0
            cache_frame(data, w, h, time, "preview:"..math.floor(time*4+0.5), "preview")
            live_prune()
            if show_thumbnail and last_seek_time and math.abs(last_seek_time-time) <= 0.25 then
                frame_cache_active = live_preview_show(last_seek_time, script_name)
            end
        end)
    end)
end

local live_capture_timer = mp.add_periodic_timer(0.5, live_capture)

-- LOCAL: local seekbar storyboard provided by a media adapter while
-- playing. It contains small BGRA tiles already sampled from the main player
-- (no extra network); showing one is a couple of file reads, so hovering over
-- already-played ranges is instant instead of waiting for a worker seek.
local storyboard_cache = { path = nil, version = nil, data = nil, shownKey = nil }
local storyboard_active = false

local function storyboard_info()
    local info = mp.get_property_native("user-data/webvideo-storyboard")
    if type(info) ~= "table" or type(info.path) ~= "string" then return nil end
    return info
end

local function storyboard_load(info)
    if not info then return nil end
    local version = tostring(info.version or "")
    if storyboard_cache.path == info.path and storyboard_cache.version == version then
        return storyboard_cache.data
    end
    local file = io.open(info.path, "rb")
    if not file then return nil end
    local json = file:read("*a")
    file:close()
    local ok, data = pcall(function() return mp.utils.parse_json(json) end)
    if not ok or type(data) ~= "table" or type(data.entries) ~= "table" or #data.entries == 0 then
        return nil
    end
    storyboard_cache.path = info.path
    storyboard_cache.version = version
    storyboard_cache.data = data
    return data
end

local function storyboard_find(data, time)
    local entries = data.entries
    if type(entries) ~= "table" or #entries == 0 then return nil end
    local best, bestDist = nil, nil
    for i = 1, #entries do
        local entry = entries[i]
        local t = tonumber(entry and entry.t)
        if t then
            local dist = math.abs(t - time)
            if bestDist == nil or dist < bestDist then
                best, bestDist = entry, dist
                if dist == 0 then break end
            end
        end
    end
    if not best then return nil end
    local interval = tonumber(data.interval) or 15
    local tolerance = math.max(interval * 0.5, 2)
    if bestDist and bestDist <= tolerance then return best end
    return nil
end

local function storyboard_show(time)
    local info = storyboard_info()
    if not info then return false end
    local data = storyboard_load(info)
    if not data then return false end
    local entry = storyboard_find(data, time)
    if not entry then
        storyboard_cache.shownKey = nil
        storyboard_active = false
        return false
    end
    -- Direct overlay from the sheet: mpv maps (offset + stride*h) bytes and
    -- scales the sub-image to dw/dh in the VO. No Lua pixel loop, no
    -- per-hover file write, so a hit costs a couple of milliseconds.
    if x == nil or y == nil then return false end
    local tw, th = tonumber(data.tileW), tonumber(data.tileH)
    local sheetW = tonumber(data.sheetW) or (tw and tw * 10) or nil
    if not tw or not th or not sheetW or tw < 1 or th < 1 then return false end
    local sheets = data.sheets
    local sheet = type(sheets) == "table" and sheets[(tonumber(entry.s) or 0) + 1] or nil
    if type(sheet) ~= "string" then return false end
    local targetW, targetH = effective_w, effective_h
    if not targetW or not targetH or targetW < 4 or targetH < 4 then targetW, targetH = tw, th end
    local scale = tonumber(options.scale_factor) or 1
    if scale ~= 1 then
        targetW = math.floor(targetW * scale + 0.5)
        targetH = math.floor(targetH * scale + 0.5)
    end
    -- Keep any existing preview worker alive (killing it would make the
    -- uncovered<->covered boundary re-spawn and re-seek constantly); the
    -- worker's own frames are suppressed from drawing while this is active.
    storyboard_cache.shownKey = tostring(entry.s) .. ":" .. tostring(entry.o)
    storyboard_active = true
    real_w, real_h = nil, nil
    last_seek_time = time
    mp.command_native_async({
        name = "overlay-add",
        id = options.overlay_id,
        x = math.floor(x + 0.5),
        y = math.floor(y + 0.5),
        file = sheet,
        offset = tonumber(entry.o) or 0,
        fmt = "bgra",
        w = tw,
        h = th,
        stride = sheetW * 4,
        dw = targetW,
        dh = targetH,
    }, function() end)
    return true
end

local function real_res(req_w, req_h, filesize)
    local count = filesize / 4
    local diff = (req_w * req_h) - count

    if (properties["video-params"] and properties["video-params"]["rotate"] or 0) % 180 == 90 then
        req_w, req_h = req_h, req_w
    end

    if diff == 0 then
        return req_w, req_h
    else
        local threshold = 5 -- throw out results that change too much
        local long_side, short_side = req_w, req_h
        if req_h > req_w then
            long_side, short_side = req_h, req_w
        end
        for a = short_side, short_side - threshold, -1 do
            if count % a == 0 then
                local b = count / a
                if long_side - b < threshold then
                    if req_h < req_w then return b, a else return a, b end
                end
            end
        end
        return nil
    end
end

local function move_file(from, to)
    if os_name == "windows" then
        os.remove(to)
    end
    -- move the file because it can get overwritten while overlay-add is reading it, and crash the player
    os.rename(from, to)
end

local function seek(fast)
    if last_seek_time then
        run("async seek " .. last_seek_time .. (fast and " absolute+keyframes" or " absolute+exact"))
    end
end

local seek_period = 3/60
local seek_period_counter = 0
local seek_timer
seek_timer = mp.add_periodic_timer(seek_period, function()
    if seek_period_counter == 0 then
        seek(allow_fast_seek)
        seek_period_counter = 1
    else
        if seek_period_counter == 2 then
            if allow_fast_seek then
                seek_timer:kill()
                seek()
            end
        else seek_period_counter = seek_period_counter + 1 end
    end
end)
seek_timer:kill()

-- Remote seeks can each reopen an HTTP connection. Send the latest stable
-- target once, instead of keyframe+exact seeks for every mouse event.
local network_seek_timer = mp.add_timeout(0.25, function() seek(false) end)
network_seek_timer:kill()

local function request_seek()
    if properties["demuxer-via-network"] or webvideo_media() then
        seek_timer:kill()
        network_seek_timer:kill()
        network_seek_timer:resume()
        return
    end
    if seek_timer:is_enabled() then
        seek_period_counter = 0
    else
        seek_timer:resume()
        seek(allow_fast_seek)
        seek_period_counter = 1
    end
end

local function check_new_thumb()
    -- the slave might start writing to the file after checking existance and
    -- validity but before actually moving the file, so move to a temporary
    -- location before validity check to make sure everything stays consistant
    -- and valid thumbnails don't get overwritten by invalid ones
    local tmp = options.thumbnail..".tmp"
    move_file(options.thumbnail, tmp)
    local finfo = mp.utils.file_info(tmp)
    if not finfo then return false end
    spawn_waiting = false
    local w, h = real_res(effective_w, effective_h, finfo.size)
    if w then -- only accept valid thumbnails
        move_file(tmp, options.thumbnail..".bgra")
        -- LOCAL: a valid frame ends the failure streak.
        worker_failures = 0
        worker_loaded = true

        real_w, real_h = w, h
        if real_w and (real_w ~= last_real_w or real_h ~= last_real_h) then
            last_real_w, last_real_h = real_w, real_h
            info(real_w, real_h)
        end
        if not show_thumbnail then
            file_timer:kill()
        end
        return true
    end

    return false
end

-- Run independently of hover/file polling so leaving the seekbar cannot
-- suspend the deadline for a stuck worker open.
local worker_health_timer = mp.add_periodic_timer(0.5, worker_health_check)

file_timer = mp.add_periodic_timer(file_check_period, function()
    if check_new_thumb() then
        -- A cached storyboard tile is on screen; never let a worker frame for
        -- an older hover target overwrite it.
        if not storyboard_active and not frame_cache_active then draw(real_w, real_h, script_name) end
    end
end)
file_timer:kill()

local function clear()
    cancel_shot()
    shot_failure_target, shot_failures = nil, 0
    file_timer:kill()
    seek_timer:kill()
    network_seek_timer:kill()
    -- Re-entering the seekbar must not display the previous hover's image.
    real_w, real_h = nil, nil
    storyboard_cache.shownKey = nil
    storyboard_active = false
    live_shown = nil
    frame_cache_active = false
    if options.quit_after_inactivity > 0 then
        if show_thumbnail or activity_timer:is_enabled() then
            activity_timer:kill()
        end
        activity_timer:resume()
    end
    last_seek_time = nil
    show_thumbnail = false
    last_x = nil
    last_y = nil
    if script_name then return end
    if pre_0_30_0 then
        mp.command_native({"overlay-remove", options.overlay_id})
    else
        mp.command_native_async({"overlay-remove", options.overlay_id}, function() end)
    end
end

local function quit()
    activity_timer:kill()
    if show_thumbnail then
        activity_timer:resume()
        return
    end
    stop_worker()
    real_w, real_h = nil, nil
    clear()
end

activity_timer = mp.add_timeout(options.quit_after_inactivity, quit)
activity_timer:kill()

local function thumb(time, r_x, r_y, script)
    if disabled then return end

    time = tonumber(time)
    if time == nil then return end

    if r_x == "" or r_y == "" then
        x, y = nil, nil
    else
        x, y = math.floor(r_x + 0.5), math.floor(r_y + 0.5)
    end

    if last_seek_time == nil or math.abs(time - last_seek_time) > 0.05 then
        -- Never relabel a known old image with the new hovered timestamp.
        real_w, real_h = nil, nil
        if not script then
            mp.command_native_async({"overlay-remove", options.overlay_id}, function() end)
        end
    end

    script_name = script
    if last_x ~= x or last_y ~= y or not show_thumbnail then
        show_thumbnail = true
        last_x, last_y = x, y
        -- While a cached storyboard tile is displayed there is no worker image
        -- to draw here (and its file may be gone), so skip the placeholder.
        if not storyboard_active and not frame_cache_active then draw(real_w, real_h, script) end
    end

    if options.quit_after_inactivity > 0 then
        if show_thumbnail or activity_timer:is_enabled() then
            activity_timer:kill()
        end
        activity_timer:resume()
    end

    if frame_capture_enabled() then
        local live = live_preview_enabled()
        if live and spawned then stop_worker() end
        frame_cache_active = live_preview_show(time, script)
        if frame_cache_active or live then
            if shot_command or shot_timer then cancel_shot() end
            if live then file_timer:kill() end
            last_seek_time = time
            return
        end
    end
    frame_cache_active = false

    -- LOCAL: already played ranges have a local storyboard tile; show it
    -- immediately instead of making the preview worker re-download segments.
    if storyboard_show(time) then
        if not file_timer:is_enabled() then file_timer:resume() end
        return
    end
    -- Successful immutable decodes remain in the session disk cache, even
    -- when this adapter uses its own storyboard for played frames.
    if shared_cached_media() then
        frame_cache_active = live_preview_show(time, script)
        last_seek_time = time
        if frame_cache_active then
            if shot_command or shot_timer then cancel_shot() end
        else
            cached_preview_request(time)
        end
        return
    end

    if time == last_seek_time then return end
    last_seek_time = time
    if not spawned then
        -- spawn already passes --start; do not immediately cancel that load.
        spawn(time)
    else
        request_seek()
    end
    if not file_timer:is_enabled() then file_timer:resume() end
end

local function watch_changes()
    if not dirty or not properties["video-out-params"] then return end
    dirty = false

    local old_w = effective_w
    local old_h = effective_h

    calc_dimensions()

    local vf_reset = vf_string(filters_reset)
    local rotate = properties["video-rotate"] or 0

    local resized = old_w ~= effective_w or
        old_h ~= effective_h or
        last_vf_reset ~= vf_reset or
        (last_rotate % 180) ~= (rotate % 180) or
        par ~= last_par or last_crop ~= properties["video-crop"]

    if resized then
        last_rotate = rotate
        info(effective_w, effective_h)
    elseif last_has_vid ~= has_vid and has_vid ~= 0 then
        info(effective_w, effective_h)
    end

    if spawned then
        if resized then
            -- mpv doesn't allow us to change output size
            local seek_time = last_seek_time
            stop_worker()
            clear()
            spawn(seek_time or mp.get_property_number("time-pos", 0))
            file_timer:resume()
        else
            if rotate ~= last_rotate then
                run("set video-rotate "..rotate)
            end
            local vf_runtime = vf_string(filters_runtime)
            if vf_runtime ~= last_vf_runtime then
                run("vf set "..vf_string(filters_all, true))
                last_vf_runtime = vf_runtime
            end
        end
    else
        last_vf_runtime = vf_string(filters_runtime)
    end

    last_vf_reset = vf_reset
    last_rotate = rotate
    last_par = par
    last_crop = properties["video-crop"]
    last_has_vid = has_vid

    if not spawned and not disabled and options.spawn_first and resized and not live_preview_enabled() then
        spawn(mp.get_property_number("time-pos", 0))
        file_timer:resume()
    end
end

local function update_property(name, value)
    properties[name] = value
end

local function update_property_dirty(name, value)
    properties[name] = value
    dirty = true
    if name == "tone-mapping" then
        last_tone_mapping = nil
    end
end

local function update_tracklist(name, value)
    -- current-tracks shim
    for _, track in ipairs(value) do
        if track.type == "video" and track.selected then
            properties["current-tracks/video"] = track
            return
        end
    end
end

local function sync_changes(prop, val)
    update_property(prop, val)
    if val == nil then return end

    if type(val) == "boolean" then
        if prop == "vid" then
            has_vid = 0
            last_has_vid = 0
            info(effective_w, effective_h)
            clear()
            return
        end
        val = val and "yes" or "no"
    end

    if prop == "vid" then
        has_vid = 1
    end

    if not spawned then return end

    run("set "..prop.." "..val)
    dirty = true
end

local function file_load()
    live_cache_reset()
    live_loaded = true
    stop_worker()
    remove_thumbnail_files()
    clear()
    spawned = false
    if worker_restart_timer then worker_restart_timer:kill() worker_restart_timer = nil end
    worker_failures = 0
    worker_loaded = false
    worker_stopping = false
    worker_generation = worker_generation + 1
    local _, marker = worker_probe_paths()
    if marker then os.remove(marker) end
    real_w, real_h = nil, nil
    last_real_w, last_real_h = nil, nil
    last_tone_mapping = nil
    last_seek_time = nil
    if info_timer then
        info_timer:kill()
        info_timer = nil
    end

    calc_dimensions()
    info(effective_w, effective_h)
end

local function shutdown()
    live_loaded = false
    live_capture_timer:kill()
    live_cache_reset()
    stop_worker()
    worker_health_timer:kill()
    remove_thumbnail_files()
    -- LOCAL: the generated probe script and its marker live next to the
    -- thumbnail in the temp directory; remove them so a closed player does
    -- not leave small stray files behind.
    local probe, marker = worker_probe_paths()
    if probe then os.remove(probe) end
    if marker then os.remove(marker) end
    if os_name ~= "windows" then
        os.remove(options.socket)
        os.remove(options.socket..".run")
    end
end

local function on_duration(prop, val)
    allow_fast_seek = (val or 30) >= 30
end

mp.observe_property("current-tracks/video", "native", function(name, value)
    if pre_0_33_0 then
        mp.unobserve_property(update_tracklist)
        pre_0_33_0 = false
    end
    update_property(name, value)
end)

mp.observe_property("track-list", "native", update_tracklist)
mp.observe_property("display-hidpi-scale", "native", update_property_dirty)
mp.observe_property("osd-dimensions", "native", update_property_dirty)
mp.observe_property("video-out-params", "native", update_property_dirty)
mp.observe_property("video-params", "native", update_property_dirty)
mp.observe_property("vf", "native", update_property_dirty)
mp.observe_property("tone-mapping", "native", update_property_dirty)
mp.observe_property("demuxer-via-network", "native", update_property)
mp.observe_property("stream-open-filename", "native", update_property)
mp.observe_property("macos-app-activation-policy", "native", update_property)
mp.observe_property("current-vo", "native", update_property)
mp.observe_property("video-rotate", "native", update_property)
mp.observe_property("video-crop", "native", update_property)
mp.observe_property("path", "native", update_property)
mp.observe_property("vid", "native", sync_changes)
mp.observe_property("edition", "native", sync_changes)
mp.observe_property("duration", "native", on_duration)

mp.register_script_message("thumb", thumb)
mp.register_script_message("clear", clear)

mp.register_event("file-loaded", file_load)
mp.register_event("start-file", function()
    live_loaded = false
    live_cache_reset()
end)
mp.register_event("shutdown", shutdown)

mp.register_idle(watch_changes)
