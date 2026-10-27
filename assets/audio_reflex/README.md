# Reflex alarm WAVs

Pre-rendered, short (< 300 ms) PCM WAV files preloaded by `VoiceSynthesisEngine.PlayEmergencyReflexAlarm()` so a VCC→GND short is announced in under 10 ms without touching the LLM or TTS pipeline.

Expected files (added in Phase 4):

- `alarm_vcc_gnd_short.wav`
- `alarm_same_strip_short.wav`
