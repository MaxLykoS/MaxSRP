"""Download Unity's CC0 six-way cumulus sample and prepare a static 2x4 atlas.

The eight cells are different cloud shapes, not animation frames. RGB is linear
lighting data; positive alpha is straight opacity. Originals stay in ignored
OfflineBaseline; only the resized runtime maps enter Assets.
"""
from pathlib import Path
import hashlib
import urllib.request
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'OfflineBaseline/CloudSixWay/Source'
DEST = ROOT / 'Assets/ThirdParty/UnitySixWayClouds'
FILES = {
    'P': ('11pFgU0xC4I1eJfifJDZqus0y967nWuGv', '07a59d738f1a79a36c5641d6e02dbfd3a71ac7ebf6750310dccd4dbc4f71f20a'),
    'N': ('1kafsQvZYvDFqMASJuTIM5hfzDNJR-J28', 'ad241e1fde892cd9ef9be1e7d7b8a6740c6e93d20677d6481eeeb86f4eb12bad'),
}

def main():
    SOURCE.mkdir(parents=True, exist_ok=True)
    DEST.mkdir(parents=True, exist_ok=True)
    for suffix, (file_id, checksum) in FILES.items():
        source = SOURCE / f'TX_Pyro_Clouds_A_2x4_{suffix}.tga'
        if not source.exists():
            urllib.request.urlretrieve(f'https://drive.usercontent.google.com/download?id={file_id}&export=download&confirm=t', source)
        if hashlib.sha256(source.read_bytes()).hexdigest() != checksum:
            raise ValueError(f'Source checksum mismatch: {source.name}')
        image = Image.open(source)
        # Resize each cell independently to preserve atlas boundaries.
        result = Image.new(image.mode, (2048, 2048))
        for row in range(4):
            for col in range(2):
                cell = image.crop((col*2048, row*1024, (col+1)*2048, (row+1)*1024))
                cell = cell.resize((1024, 512), Image.Resampling.LANCZOS)
                result.paste(cell, (col*1024, row*512))
        result.save(DEST / f'Clouds_{suffix}.png')
        print(f'Prepared Clouds_{suffix}.png: {result.size}, {result.mode}')

if __name__ == '__main__':
    main()
