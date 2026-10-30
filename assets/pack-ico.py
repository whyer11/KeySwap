import struct
import os
import sys

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
png_dir = os.path.join(root, 'assets', 'png')
out = os.path.join(root, 'assets', 'KeySwap.ico')

sizes = [16, 24, 32, 48, 64, 128, 256]
datas = []
for s in sizes:
    p = os.path.join(png_dir, f'{s}.png')
    with open(p, 'rb') as f:
        datas.append(f.read())

header = struct.pack('<HHH', 0, 1, len(datas))
offset = 6 + 16 * len(datas)
entries = b''
for s, d in zip(sizes, datas):
    dim = 0 if s >= 256 else s
    entries += struct.pack('<BBBBHHII', dim, dim, 0, 0, 1, 32, len(d), offset)
    offset += len(d)

with open(out, 'wb') as f:
    f.write(header + entries + b''.join(datas))

print('wrote', out, os.path.getsize(out), 'bytes,', len(datas), 'sizes')
