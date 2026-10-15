# CMF Buds Pro 2 protocol notes

How MuggleBuds talks to CMF Buds Pro 2, and what has and hasn't been checked on real buds. The
Pixel Buds code from MagicBuds (`src/MuggleBuds.Presentation/Maestro`, `PixelBudsHeadset`) is still in
the tree but isn't used; its notes are in [notes.md](notes.md).

## If something doesn't work

1. Settings › App › turn on **Debug logging**.
2. Reproduce the problem, then Settings › App › **Open log folder**.
3. Send the newest `.log`. Every byte read and written is in it (`Read ...`, `Wrote ...`, `Sent ...`,
   `Received ...`), which is enough to see what the buds actually say.

## Protocol (from radiance-project/ear-web, `res/js/bluetooth_socket.js`, checked against real buds)

- RFCOMM service `aeac4a03-dff5-498f-843a-34487cf133eb`.
- **Frames sent to the buds:** `55 60 01 | command (LE16) | payload length | 00 | operation | payload | CRC16 (LE)`.
  CRC16 is Modbus (init `FFFF`, polynomial `A001`) over every byte before it.
- **Replies:** the same layout, but the CRC covers the **payload only**. The command is the request's
  with the top bit cleared (`0xC007` is answered by `0x4007`).
- **Pushes** (the buds telling you about a change by themselves) start `55 00 01`, have the operation
  byte `00` and **no CRC**. `0xE001` battery, `0xE003` noise control. `0xE002` and `0xE00E` also
  show up and are ignored.
- Reads: `0xC007` battery, `0xC00E` settings list (first value = in-ear detection), `0xC01E` noise
  control, `0xC041` low latency, `0xC042` firmware, `0xC04E` enhanced bass, `0xC050` listening mode.
- Sets: `0xF00F` noise control `[01, mode, 00]`, `0xF004` in-ear `[01, 01, on]`, `0xF040` low latency
  `[01 on / 02 off, 00]`, `0xF051` enhanced bass `[on, level]`, `0xF01D` listening mode `[mode, 00]`.
  A set is answered with command `0x700F` (the same for all of them) and an empty payload.
- Noise control values (set and read): `01` cancellation high, `02` mid, `03` low, `04` adaptive,
  `05` off, `07` transparency. A read's reply is a list of `(setting, value, 00)` entries,
  e.g. `02 03 00 01 05 00`: **the mode is setting 1**, not the first entry.
- Listening mode: `00` Dirac Opteo, `03` Pop, `01` Rock, `05` Classical, `02` Electronic,
  `04` Enhance vocals, `06` Custom.

## Notes

- **Buds variant.** ear-web tells B172, B187 and B195 apart from the Bluetooth Fast Pair id, which
  isn't available over RFCOMM, so the app doesn't detect it. They differ in how enhanced bass levels
  are counted. The app only echoes back the level the buds last reported, and refuses to turn bass
  on while that level is 0. Buds that reported 6 and 10 count like B172 (`level * 2`).
- ear-web says Dirac Opteo isn't available while LDAC is on; the buds may ignore that set. The
  setting is read back after 300 ms, so the app shows what the buds really hold.
- Tray icon pinning changes `IsPromoted` under `HKCU\Control Panel\NotifyIconSettings` for this
  app's exe (Windows 11). Settings › App › "Show in the tray while connected" turns it off.

## Not verified on real buds

- Listening mode sets, and the Custom mode (it uses whatever custom EQ is saved in the Nothing X app).
- The other pushes (`0xE002`, `0xE00E`) are ignored; what they carry is unknown.

## Licenses

MuggleBuds is AGPL-3.0 (see `LICENSE`). The protocol details come from
[ear-web](https://github.com/radiance-project/ear-web), also AGPL-3.0, and the app is built on
[MagicBuds](https://github.com/artistro08/MagicBuds) (MIT), see `LICENSE-THIRD-PARTY`.
