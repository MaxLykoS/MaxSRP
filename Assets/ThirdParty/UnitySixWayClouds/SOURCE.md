# Unity six-way cloud sample

Originals: `TX_Pyro_Clouds_A_2x4_P.tga` and `TX_Pyro_Clouds_A_2x4_N.tga`.

Unity's official [sample documentation](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@17.0/manual/create-effects-with-six-way-lighting.html) explicitly distributes the sample library under **CC0**:
https://creativecommons.org/publicdomain/zero/1.0/

Official sample library:
https://drive.google.com/drive/folders/1_oh0UkAOW6hISqouCXjYwKQ0lkoj0CIF

Downloaded 2026-10-30. Original SHA-256:

- P: `07a59d738f1a79a36c5641d6e02dbfd3a71ac7ebf6750310dccd4dbc4f71f20a`
- N: `ad241e1fde892cd9ef9be1e7d7b8a6740c6e93d20677d6481eeeb86f4eb12bad`

`Tools/PrepareUnitySixWayClouds.py` verifies the originals and resizes each cell independently from 2048x1024 to 1024x512, preserving eight static cloud variants in a 2x4 atlas. Originals remain outside Assets in ignored OfflineBaseline. No six-way lighting is generated from the old planar cloud map.

P.rgb = Right, Top, Back. N.rgb = Left, Bottom, Front. P.a = opacity; N has no alpha. These are linear lighting coefficients, not sRGB color. No emission is used.

The sample textures are CC0. This project supplies its own MaxSRP rendering code and does not require VFX Graph or HDRP.
