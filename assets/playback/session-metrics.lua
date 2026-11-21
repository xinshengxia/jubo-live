-- Publish only player counters. Alternating slots keep one complete sample
-- available while the other is being written, including on Windows.
local mp = require "mp"
local state = (mp.get_property_native("options/script-opts") or {}).live_title_state_file
if not state or state == "" then return end
local pid = mp.get_property_number("pid")
if not pid then return end
local loaded = false
local function number(property, scale, format, signed)
    local value = mp.get_property_number(property)
    if not value or value ~= value or math.abs(value) == math.huge or (not signed and value < 0) then return "-" end
    return string.format(format, value * scale)
end
local function sample()
    local now = os.time()
    local values = {"-", "-", "-", "-"}
    if loaded and not mp.get_property_native("idle-active") then
        values[1] = number("avsync", 1000, "%.3f", true)
        values[2] = number("decoder-frame-drop-count", 1, "%.0f")
        values[3] = number("frame-drop-count", 1, "%.0f")
        values[4] = number("demuxer-cache-duration", 1, "%.3f")
    end
    local f = io.open(state .. ".metrics" .. (now % 2), "wb")
    if f then
        f:write(string.format("v1 %d %d %s\n", now, pid, table.concat(values, " ")))
        f:close()
    end
end
mp.register_event("file-loaded", function() loaded = true; sample() end)
mp.register_event("end-file", function() loaded = false; sample() end)
mp.add_periodic_timer(1, sample)
mp.register_event("shutdown", function()
    os.remove(state .. ".metrics0")
    os.remove(state .. ".metrics1")
end)
