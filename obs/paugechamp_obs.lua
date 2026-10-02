--[[
PaugeChamp for OBS Studio
Add via Tools > Scripts > "+" and pick this file. PaugeChamp must be running.

 * Change the HD PVR's input, audio, quality and picture settings from inside OBS.
 * One click adds a Media Source that plays the HD PVR's stream.
 * Start / stop a lossless recording (MP4 or .ts) straight from the HD PVR's encoder.
]]

local obs = obslua
local ffi = require("ffi")

ffi.cdef [[
typedef int BOOL;
typedef unsigned long DWORD;
BOOL CallNamedPipeA(const char *name, const char *inBuf, DWORD inSize, char *outBuf, DWORD outSize,
                    DWORD *bytesRead, DWORD timeout);
DWORD GetLastError(void);
]]

local PIPE = "\\\\.\\pipe\\paugechamp"
local SOURCE_NAME = "HD PVR 1212"

local DEVICE_KEYS = { "video_input", "audio_input", "audio_codec", "video_standard", "bitrate_mode",
                      "bitrate", "peak_bitrate", "audio_boost",
                      "brightness", "contrast", "hue", "saturation", "sharpness", "record_format" }

local known = {}          -- OBS-side values as of the last sync, so only real user changes are sent
local current_settings = nil
local status_text = "Not checked yet"
local udp_port = 5004
local max_hue = 255

-- ---------------------------------------------------------------- pipe

local function call(request)
    local out = ffi.new("char[65536]")
    local n = ffi.new("DWORD[1]")
    local ok = ffi.C.CallNamedPipeA(PIPE, request, #request, out, 65536, n, 3000)
    if ok == 0 then
        return nil, "PaugeChamp is not running (start it first)"
    end
    local reply = ffi.string(out, n[0])
    local first, rest = reply:match("^([^\n]*)\n?(.*)$")
    if first ~= "OK" then
        return nil, first
    end
    local values = {}
    for k, v in rest:gmatch("([%w_]+)=([^\n]*)") do
        values[k] = v
    end
    return values
end

-- ---------------------------------------------------------------- settings <-> OBS data

local function to_text(settings, key)
    if key == "bitrate" or key == "peak_bitrate" then
        return string.format("%.1f", obs.obs_data_get_double(settings, key))
    elseif key == "audio_boost" then
        return obs.obs_data_get_bool(settings, key) and "1" or "0"
    elseif key == "brightness" or key == "contrast" or key == "hue" or key == "saturation" or key == "sharpness" then
        return tostring(obs.obs_data_get_int(settings, key))
    end
    return obs.obs_data_get_string(settings, key)
end

local function from_text(settings, key, value)
    if key == "bitrate" or key == "peak_bitrate" then
        obs.obs_data_set_double(settings, key, tonumber(value) or 0)
    elseif key == "audio_boost" then
        obs.obs_data_set_bool(settings, key, value == "1")
    elseif key == "brightness" or key == "contrast" or key == "hue" or key == "saturation" or key == "sharpness" then
        obs.obs_data_set_int(settings, key, tonumber(value) or 0)
    else
        obs.obs_data_set_string(settings, key, value)
    end
end

local function snapshot(settings)
    for _, k in ipairs(DEVICE_KEYS) do
        known[k] = to_text(settings, k)
    end
end

-- Copies the app's current settings into OBS so both sides agree.
local function pull(settings)
    local values, err = call("get")
    if values then
        for _, k in ipairs(DEVICE_KEYS) do
            if values[k] then from_text(settings, k, values[k]) end
        end
        udp_port = tonumber(values["udp_port"]) or udp_port
        max_hue = tonumber(values["max_hue"]) or max_hue
    else
        status_text = err
    end
    snapshot(settings)
    return values ~= nil
end

local function refresh_status()
    local st, err = call("status")
    if not st then
        status_text = err
        return
    end
    local parts = { st.message ~= "" and st.message or st.state }
    if st.signal and st.signal ~= "none" then table.insert(parts, "Input: " .. st.signal) end
    if st.streaming == "1" then table.insert(parts, st.mbps .. " Mbps") end
    if st.recording == "1" then table.insert(parts, "Recording: " .. st.record_path) end
    status_text = table.concat(parts, "\n")
    local port = st.stream_url and st.stream_url:match(":(%d+)$")
    udp_port = tonumber(port) or udp_port
end

-- ---------------------------------------------------------------- media source

local function stream_url()
    -- overrun_nonfatal + fifo keep FFmpeg reading through hiccups (input changes, restarts).
    return "udp://127.0.0.1:" .. udp_port .. "?overrun_nonfatal=1&fifo_size=1000000"
end

local function add_source(props, prop)
    refresh_status()
    local scene_source = obs.obs_frontend_get_current_scene()
    if scene_source == nil then return false end
    local scene = obs.obs_scene_from_source(scene_source)

    local source = obs.obs_get_source_by_name(SOURCE_NAME)
    local s = obs.obs_data_create()
    obs.obs_data_set_bool(s, "is_local_file", false)
    obs.obs_data_set_string(s, "input", stream_url())
    obs.obs_data_set_string(s, "input_format", "mpegts")
    obs.obs_data_set_int(s, "buffering_mb", 1)
    obs.obs_data_set_int(s, "reconnect_delay_sec", 1)
    obs.obs_data_set_bool(s, "hw_decode", true)
    obs.obs_data_set_bool(s, "close_when_inactive", false)
    obs.obs_data_set_bool(s, "restart_on_activate", false)
    obs.obs_data_set_bool(s, "clear_on_media_end", false)
    if source == nil then
        source = obs.obs_source_create("ffmpeg_source", SOURCE_NAME, s, nil)
    else
        obs.obs_source_update(source, s)
    end
    obs.obs_data_release(s)

    if obs.obs_scene_find_source(scene, SOURCE_NAME) == nil then
        obs.obs_scene_add(scene, source)
    end
    obs.obs_source_release(source)
    obs.obs_source_release(scene_source)
    return false
end

-- ---------------------------------------------------------------- buttons

local function update_status_property(props)
    local p = obs.obs_properties_get(props, "status")
    if p ~= nil then obs.obs_property_set_description(p, status_text) end
end

local function on_refresh(props, prop)
    if current_settings ~= nil then pull(current_settings) end
    refresh_status()
    update_status_property(props)
    return true
end

local function simple_command(cmd)
    return function(props, prop)
        local _, err = call(cmd)
        refresh_status()
        if err then status_text = err end
        update_status_property(props)
        return true
    end
end

-- ---------------------------------------------------------------- OBS script API

function script_description()
    return "<b>PaugeChamp</b><br>Controls the Hauppauge HD PVR through <i>PaugeChamp</i>, " ..
           "which must be running. Changes apply to the device immediately; changing an input " ..
           "restarts the stream (about 5 seconds)."
end

function script_properties()
    local props = obs.obs_properties_create()

    local info = obs.obs_properties_add_text(props, "status", "Status", obs.OBS_TEXT_INFO)
    obs.obs_property_set_description(info, status_text)

    local p = obs.obs_properties_add_list(props, "video_input", "Video input", obs.OBS_COMBO_TYPE_LIST, obs.OBS_COMBO_FORMAT_STRING)
    obs.obs_property_list_add_string(p, "Component (YPbPr)", "component")
    obs.obs_property_list_add_string(p, "S-Video", "svideo")
    obs.obs_property_list_add_string(p, "Composite", "composite")

    p = obs.obs_properties_add_list(props, "audio_input", "Audio input", obs.OBS_COMBO_TYPE_LIST, obs.OBS_COMBO_FORMAT_STRING)
    obs.obs_property_list_add_string(p, "RCA - rear", "rca_back")
    obs.obs_property_list_add_string(p, "RCA - front", "rca_front")
    obs.obs_property_list_add_string(p, "Optical (S/PDIF)", "spdif")

    p = obs.obs_properties_add_list(props, "audio_codec", "Audio format", obs.OBS_COMBO_TYPE_LIST, obs.OBS_COMBO_FORMAT_STRING)
    obs.obs_property_list_add_string(p, "AAC", "aac")
    obs.obs_property_list_add_string(p, "AC-3 (Dolby Digital)", "ac3")

    p = obs.obs_properties_add_list(props, "video_standard", "Analog standard", obs.OBS_COMBO_TYPE_LIST, obs.OBS_COMBO_FORMAT_STRING)
    obs.obs_property_list_add_string(p, "NTSC / 60 Hz", "ntsc")
    obs.obs_property_list_add_string(p, "PAL / 50 Hz", "pal")

    obs.obs_properties_add_bool(props, "audio_boost", "Boost analog audio level")

    p = obs.obs_properties_add_list(props, "bitrate_mode", "Bitrate mode", obs.OBS_COMBO_TYPE_LIST, obs.OBS_COMBO_FORMAT_STRING)
    obs.obs_property_list_add_string(p, "Constant (CBR)", "cbr")
    obs.obs_property_list_add_string(p, "Variable (VBR)", "vbr")
    obs.obs_property_list_add_string(p, "Variable, peak-limited", "vbr_peak")

    obs.obs_properties_add_float_slider(props, "bitrate", "Average bitrate (Mbps)", 1.0, 13.5, 0.1)
    obs.obs_properties_add_float_slider(props, "peak_bitrate", "Peak bitrate (Mbps, VBR)", 1.1, 20.2, 0.1)

    obs.obs_properties_add_int_slider(props, "brightness", "Brightness", 0, 255, 1)
    obs.obs_properties_add_int_slider(props, "contrast", "Contrast", 0, 255, 1)
    obs.obs_properties_add_int_slider(props, "hue", "Hue", 0, max_hue, 1)
    obs.obs_properties_add_int_slider(props, "saturation", "Saturation", 0, 255, 1)
    obs.obs_properties_add_int_slider(props, "sharpness", "Sharpness", 0, 255, 1)

    obs.obs_properties_add_button(props, "add_source", "Add HD PVR source to current scene", add_source)
    obs.obs_properties_add_button(props, "refresh", "Refresh status / reload settings", on_refresh)
    p = obs.obs_properties_add_list(props, "record_format", "HD PVR recording format", obs.OBS_COMBO_TYPE_LIST, obs.OBS_COMBO_FORMAT_STRING)
    obs.obs_property_list_add_string(p, "MP4", "mp4")
    obs.obs_property_list_add_string(p, "MPEG-TS (.ts)", "ts")
    obs.obs_properties_add_button(props, "rec_start", "Start HD PVR recording (lossless, no re-encode)", simple_command("record start"))
    obs.obs_properties_add_button(props, "rec_stop", "Stop HD PVR recording", simple_command("record stop"))
    return props
end

function script_defaults(settings)
    obs.obs_data_set_default_string(settings, "video_input", "component")
    obs.obs_data_set_default_string(settings, "audio_input", "rca_back")
    obs.obs_data_set_default_string(settings, "audio_codec", "aac")
    obs.obs_data_set_default_string(settings, "video_standard", "ntsc")
    obs.obs_data_set_default_string(settings, "bitrate_mode", "cbr")
    obs.obs_data_set_default_string(settings, "record_format", "mp4")
    obs.obs_data_set_default_double(settings, "bitrate", 10.0)
    obs.obs_data_set_default_double(settings, "peak_bitrate", 13.5)
    obs.obs_data_set_default_int(settings, "brightness", 128)
    obs.obs_data_set_default_int(settings, "contrast", 64)
    obs.obs_data_set_default_int(settings, "hue", 15)
    obs.obs_data_set_default_int(settings, "saturation", 64)
    obs.obs_data_set_default_int(settings, "sharpness", 128)
end

function script_load(settings)
    current_settings = settings
    -- The app is the source of truth at startup; OBS only pushes changes the user makes here.
    pull(settings)
    refresh_status()
end

function script_update(settings)
    current_settings = settings
    local lines = {}
    for _, k in ipairs(DEVICE_KEYS) do
        local v = to_text(settings, k)
        if v ~= "" and v ~= known[k] then
            table.insert(lines, k .. "=" .. v)
            known[k] = v
        end
    end
    if #lines == 0 then return end

    local _, err = call("set\n" .. table.concat(lines, "\n"))
    if err then status_text = err end
end
