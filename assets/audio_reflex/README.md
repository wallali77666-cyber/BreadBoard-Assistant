# Reflex alarm folder

Reserved for optional operator-supplied alarm WAVs. The application does **not** ship or
require any file here: `VoiceSynthesisEngine` synthesizes the short-circuit alarm as an
in-memory PCM WAV on first use (`GenerateAlarmWav`) and caches it in a `WaveOutEvent`
buffer, so a VCC→GND short is announced in well under 10 ms without touching the LLM or
the TTS pipeline.

If you prefer your own sound, drop a 16-bit PCM WAV here and point
`BREADBOARD_VOICE_ALARM_WAV` at it (defaults to `alarm_vcc_gnd_short.wav` in this folder).
Unreadable or unsupported files are ignored and the runtime-synthesized alarm is used instead.
