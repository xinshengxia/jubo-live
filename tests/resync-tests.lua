local real = require "mp"
local options = real.get_property_native("options/script-opts")
local room_file = options.recovery_directory .. "/synthetic-room-state.txt"
local relay_file = options.recovery_directory .. "/synthetic-relay-state.txt"
local function write(path, text)
    local f = assert(io.open(path, "wb")); f:write(text); f:close()
end

local function fixture(in_session)
    write(room_file, "LIVE"); write(relay_file, "")
    local target = "https://example.invalid/synthetic"
    local props = {path=target, idle="no", pause=false, ["time-pos"]=0}
    local callbacks, bindings, ticks, loads = {}, {}, {}, {}
    local state = {now=0, remaining_errors=0, props=props, loads=loads}
    local fake = {
        get_property_native=function(key)
            if key == "options/script-opts" then
                return in_session and {live_title_state_file=room_file, live_title_relay_file=relay_file} or {}
            end
            return props[key]
        end,
        get_property=function(k) return props[k] end,
        get_property_number=function(k) return props[k] end,
        set_property=function(k,v) props[k]=v end,
        get_time=function() return state.now end,
        osd_message=function(text) state.message=text end,
        register_event=function(k,fn) callbacks[k]=fn end,
        add_key_binding=function(_,key,fn) bindings[key]=fn end,
        add_periodic_timer=function(_,fn) ticks[#ticks+1]=fn;return {kill=function() end} end,
        commandv=function(command,path)
            if command ~= "loadfile" then return end
            loads[#loads+1]=state.now
            callbacks["end-file"]({reason="stop"})
            props.path=path; callbacks["start-file"]()
            if state.remaining_errors>0 then
                state.remaining_errors=state.remaining_errors-1
                props.path=nil; callbacks["end-file"]({reason="error"})
            else
                props["eof-reached"]=false;props["time-pos"]=0;callbacks["file-loaded"]()
            end
        end
    }
    package.loaded.mp=fake
    dofile(options.recovery_source)
    callbacks["start-file"]();callbacks["file-loaded"]()
    function state:tick(seconds)
        for _=1,seconds do
            self.now=self.now+1
            if props.path and not props.pause and not props["eof-reached"] then
                props["time-pos"]=props["time-pos"]+1
            end
            for _,fn in ipairs(ticks) do fn() end
        end
    end
    function state:eof() props["eof-reached"]=true end
    function state:stop() callbacks["end-file"]({reason="stop"}); props.path=nil end
    function state:change() callbacks["end-file"]({reason="stop"}); props.path="next-local-file"; callbacks["start-file"](); callbacks["file-loaded"]() end
    function state:quit() callbacks.shutdown() end
    function state:manual() bindings.reload() end
    return state
end

local ok,err=pcall(function()
    local s=fixture(false);s:eof();s:tick(100);assert(#s.loads==0 and s.props.idle=="no","normal mpv file must not auto-reload")
    s:manual();assert(#s.loads==1,"ordinary explicit binding remains available")
    s=fixture(true);s:eof();s.props.pause=true;s:tick(60);assert(#s.loads==0,"respect explicit pause")
    s:manual();assert(#s.loads==1 and s.props.pause,"manual refresh preserves deliberate pause")
    s.props.pause=false;s:eof();s:tick(4);assert(#s.loads==2 and not s.props.pause,"resume after clean EOF")
    s=fixture(true);s:eof();write(room_file,"OFFLINE");s:tick(90);assert(#s.loads==0,"confirmed offline waits")
    write(room_file,"LIVE");s:tick(4);assert(#s.loads==1,"positive live status resumes")
    s:eof();write(relay_file,"REPLAY-UNAVAILABLE|synthetic");s:tick(80);assert(#s.loads==1,"unavailable replay waits")
    write(relay_file,"VERIFICATION");s:tick(80);assert(#s.loads==1,"verification waits")
    write(relay_file,"");write(room_file,"LOOP");s:tick(12);assert(#s.loads==2,"playable replay is recoverable")
    s=fixture(true);write(room_file,"UNKNOWN");s.remaining_errors=7;s:eof();s:tick(320)
    assert(#s.loads==8 and s.props.path and s.loads[7]-s.loads[6]>=60,"long outage backs off and later recovers")
    s:tick(18);local now=s.now;s:eof();s:tick(4);assert(#s.loads==9 and s.loads[9]-now<=4,"sustained media resets delay")
    for _,action in ipairs({"stop","change","quit"}) do
        s=fixture(true);s:eof();s:tick(1);s[action](s);s:eof();s:tick(80)
        assert(#s.loads==0 and s.props.idle=="no","cancel after "..action)
    end
end)
package.loaded.mp=real
if not ok then real.msg.error(tostring(err)) end
real.msg.info(ok and "PASS: session EOF, pause, manual reload, offline/replay, long outage and cancellation" or "FAIL: recovery")
real.commandv("quit",ok and 0 or 1)
