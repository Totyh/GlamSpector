# Bundled card-rendering fallback font

GlamSpector embeds the static **Noto Sans Regular** and **Noto Sans Bold**
TrueType faces as its last-resort card-rendering font. System fonts remain
preferred; these resources allow card rendering when SixLabors.Fonts cannot
discover any usable system family, including some Wine environments.

The files came from the official archived `notofonts/noto-fonts` repository at
commit `ffebf8c1ee449e544955a7e813c54f9b73848eac`:

- `hinted/ttf/NotoSans/NotoSans-Regular.ttf`
- `hinted/ttf/NotoSans/NotoSans-Bold.ttf`
- `LICENSE`

They are distributed under the SIL Open Font License 1.1. The unmodified
license text is kept as `NotoSans-OFL.txt` and included in the plugin package.

SHA-256:

- Regular: `B85C38ECEA8A7CFB39C24E395A4007474FA5A4FC864F6EE33309EB4948D232D5`
- Bold: `C976E4B1B99EDC88775377FCC21692CA4BFA46B6D6CA6522BFDA505B28FF9D6A`
- License: `0DAB92D0544F7B233403F14B84A663BDBFA746982EDA629E7F4F9FFE1B036FEB`
