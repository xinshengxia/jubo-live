-- Resolve the original room on every open, including reconnects. The normal
-- ytdl_hook handles the room if the official API cannot supply a media URL.
local room = os.getenv("JUBO_BILI_ROOM")
local resolver = os.getenv("JUBO_BILI_RESOLVER")
if not room or not room:match("^%d+$") or not resolver then return end
mp.add_hook("on_load", 1, function(hook)
    if mp.get_property("path") ~= "https://live.bilibili.com/" .. room then return end
    hook:defer()
    mp.command_native_async({
        name = "subprocess", playback_only = true, capture_stdout = true,
        args = {resolver, "--bili-native-url", room, os.getenv("JUBO_BILI_QUALITY") or "best",
                os.getenv("JUBO_BILI_COOKIE_FILE") or ""}
    }, function(ok, result)
        local url = ok and result and result.status == 0 and (result.stdout or ""):match("^(https?://%S+)%s*$")
        if url then
            mp.set_property("stream-open-filename", url)
            mp.set_property("file-local-options/ytdl", "no")
            mp.set_property("file-local-options/referrer", "https://live.bilibili.com/")
        end
        hook:cont()
    end)
end)
