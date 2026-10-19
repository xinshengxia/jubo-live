-- Hidden synthetic-media probe, run only by Test-MpvLiveSync.ps1.
local mp = require 'mp'
local utils = require 'mp.utils'
local opts = mp.get_property_native('options/script-opts') or {}
local seconds = tonumber(opts.sync_test_seconds) or 15
local report_path = assert(opts.sync_test_report, 'missing report path')
local started_at = nil
local finished = false
local samples = {}
local failures = {}
local max_abs = 0
local total_abs = 0
local last_time_pos = nil
local function fail(message)
    if not failures[message] then failures[message] = true end
end
local function finite(value)
    return type(value) == 'number' and value == value and math.abs(value) < math.huge
end
local function finish()
    if finished then return end
    finished = true
    if not started_at then fail('file-loaded event missing') end
    if #samples < math.floor((seconds - 2) * 2) then fail('insufficient valid audio/video samples') end
    if not last_time_pos or last_time_pos < seconds - 1 then fail('playback did not advance in real time') end
    if max_abs > 0.15 then fail('synthetic A/V difference exceeded 150 ms after startup') end
    local errors = {}
    for message in pairs(failures) do errors[#errors + 1] = message end
    table.sort(errors)
    local report = {
        ok = #errors == 0,
        case = opts.sync_test_case or 'synthetic',
        seconds = seconds,
        videoSync = mp.get_property('video-sync'),
        framedrop = mp.get_property('framedrop'),
        videoOutput = mp.get_property('current-vo'),
        audioOutput = mp.get_property('current-ao'),
        validSamples = #samples,
        maxAbsAvsync = max_abs,
        meanAbsAvsync = #samples > 0 and total_abs / #samples or 0,
        lastTimePos = last_time_pos,
        errors = errors,
        samples = samples,
    }
    local file = assert(io.open(report_path, 'wb'))
    file:write(utils.format_json(report))
    file:close()
    mp.commandv('quit', report.ok and 0 or 1)
end
mp.register_event('file-loaded', function()
    started_at = mp.get_time()
    mp.add_timeout(seconds, finish)
end)
mp.add_periodic_timer(0.25, function()
    if finished or not started_at then return end
    if mp.get_property('video-sync') ~= 'audio' then fail('video-sync policy overridden') end
    if mp.get_property('framedrop') ~= 'vo' then fail('framedrop policy overridden') end
    if mp.get_time() - started_at < 2 then return end
    if mp.get_property('current-vo') ~= 'null' then fail('video output is not null') end
    if mp.get_property('current-ao') ~= 'null' then fail('audio output is not null') end
    local difference = mp.get_property_number('avsync')
    local pos = mp.get_property_number('time-pos')
    local audio_pos = mp.get_property_number('audio-pts')
    if finite(difference) and finite(pos) and finite(audio_pos) then
        max_abs = math.max(max_abs, math.abs(difference))
        total_abs = total_abs + math.abs(difference)
        last_time_pos = pos
        samples[#samples + 1] = { elapsed = mp.get_time() - started_at, avsync = difference, timePos = pos, audioPts = audio_pos }
    end
end)
mp.add_timeout(seconds + 8, finish)
